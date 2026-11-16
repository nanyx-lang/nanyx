namespace NyxCompiler

open System
open System.IO
open Microsoft.Extensions.FileSystemGlobbing
open Parser.Program

/// A package on disk: its manifest, its source files, the one that declares its `@entry`, if any, and the packages it
/// depends on, each with its own.
type Package =
    { Manifest: Manifest
      /// Full paths, in order.
      Sources: string list
      Entry: string option
      /// In the order its manifest lists them.
      Dependencies: Package list }

/// What building a package found: each source file's compile, and every diagnostic once. Each module is checked once
/// for the whole build, though a file imported by another is compiled as part of both.
type PackageBuild =
    { Package: Package
      Results: (string * CompileResult) list
      Diagnostics: Diagnostic list
      /// Whether every error in the package is a type or name error deferred to run time (see
      /// `CompileResult.ErrorsDeferred`), so a debug build can run its `@entry`. True when there are none.
      ErrorsDeferred: bool }

module Package =
    /// The files the manifest's `sources` select, as full paths in order.
    let sources (manifest: Manifest) : string list =
        let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
        matcher.AddIncludePatterns manifest.Sources
        matcher.GetResultsInFullPath manifest.Root |> Seq.map Path.GetFullPath |> Seq.sort |> List.ofSeq

    /// Whether the manifest's `sources` select the file at the full path `filePath`, which need not exist yet.
    let private selects (manifest: Manifest) (filePath: string) =
        let relative = Path.GetRelativePath(manifest.Root, filePath)
        let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
        matcher.AddIncludePatterns manifest.Sources
        not (Path.IsPathRooted relative || relative.StartsWith "..") && matcher.Match(relative).HasMatches

    /// Whether the module at `path` declares an `@entry`. A file that does not parse does not; building it reports why.
    let private declaresEntry (path: string) =
        match parseModuleFile path (File.ReadAllText path) with
        | Result.Ok items -> items |> List.exists (function Kind (Attribute(name, _)) -> name = Attributes.entryAttribute | _ -> false)
        | Result.Error _ -> false

    /// The packages a build has read so far, by name, each with its manifest's path: a build has one of each.
    type private Reached = Collections.Generic.Dictionary<string, string>

    /// The manifest of the package in `directory`, the one `.nyxp` file there, or what is wrong.
    let private manifestIn (directory: string) : Result<string, string> =
        if not (Directory.Exists directory) then
            Result.Error $"{directory} is not a directory"
        else
            match Directory.GetFiles(directory, "*" + Manifest.extension) |> Array.sort with
            | [| manifest |] -> Result.Ok(Path.GetFullPath manifest)
            | [||] -> Result.Error $"{directory} has no {Manifest.extension} manifest"
            | several -> Result.Error $"""{directory} has more than one {Manifest.extension} manifest: {several |> Array.map Path.GetFileName |> String.concat ", "}"""

    /// What is wrong with a package of a build: a diagnostic at the place in a manifest it is about, or a message
    /// about the package as a whole.
    type private Problem =
        | AtManifest of Diagnostic
        | AboutPackage of string

    let private describe (problem: Problem) =
        match problem with
        | AtManifest diagnostic -> Manifest.describe diagnostic
        | AboutPackage message -> message

    /// The errors of each of `results`, or the values of all of them.
    let private all (results: Result<'a, Problem list> list) : Result<'a list, Problem list> =
        match results |> List.collect (function Result.Error errors -> errors | Result.Ok _ -> []) with
        | [] -> Result.Ok(results |> List.choose (function Result.Ok value -> Some value | Result.Error _ -> None))
        | errors -> Result.Error(List.distinct errors)

    /// An error at each entry of the manifest's `publicModules` that names none of the modules `files`, the package's
    /// source files, declare.
    let private publicModuleErrors (manifest: Manifest) (files: string list) : Diagnostic list =
        Manifest.unknownPublicModules manifest (PackageModules.ofFiles manifest.Name manifest.Root manifest.Host files).Modules.Keys

    /// The package `manifest` describes, with the packages it depends on. `chain` is the manifests of the packages
    /// that led to it, nearest first, each with its package's name, and `reached` every package the build has read so far.
    let rec private ofManifestIn (reached: Reached) (chain: (string * string) list) (manifest: Manifest) : Result<Package, Problem list> =
        let files = sources manifest
        let display (file: string) = Path.GetRelativePath(manifest.Root, file)
        let entry =
            if files.IsEmpty then
                Result.Error [ AboutPackage $"""{manifest.Path}: no source files match {manifest.Sources |> List.map (sprintf "'%s'") |> String.concat ", "}""" ]
            else
                match files |> List.filter declaresEntry with
                | [] -> Result.Ok None
                | [ entry ] -> Result.Ok(Some entry)
                | several ->
                    Result.Error [ AboutPackage $"""{manifest.Path}: a package can have one @entry, but these files each declare one: {several |> List.map display |> String.concat ", "}""" ]
        let publicModules = publicModuleErrors manifest files |> List.map AtManifest
        match entry, dependenciesIn reached ((manifest.Path, manifest.Name) :: chain) manifest with
        | Result.Ok entry, Result.Ok dependencies when publicModules.IsEmpty ->
            Result.Ok { Manifest = manifest; Sources = files; Entry = entry; Dependencies = dependencies }
        | entry, dependencies ->
            let errorsOf = function Result.Error errors -> errors | Result.Ok _ -> []
            Result.Error(errorsOf entry @ publicModules @ errorsOf dependencies)

    /// The packages `manifest` depends on, each read with its own dependencies, or what is wrong: a directory with no
    /// manifest, a package named other than its dependant names it, one that depends on itself through others, or one
    /// reached by two paths. `chain` holds `manifest` itself first.
    and private dependenciesIn (reached: Reached) (chain: (string * string) list) (manifest: Manifest) : Result<Package list, Problem list> =
        let error code span message = Result.Error [ AtManifest(Diagnostics.at code span message) ]
        let read (dependency: Dependency) =
            match manifestIn dependency.Directory with
            | Result.Error message -> error DiagnosticCodes.InvalidManifestValue dependency.PathSpan $"the dependency {dependency.Name} can't be read: {message}"
            | Result.Ok path ->
                match Manifest.read path with
                | Result.Error errors -> Result.Error(errors |> List.map AtManifest)
                | Result.Ok found when found.Name <> dependency.Name ->
                    error
                        DiagnosticCodes.InvalidManifestValue
                        dependency.NameSpan
                        $"the package in {dependency.Directory} is named {found.Name}, not {dependency.Name}: the names must match"
                | Result.Ok found when chain |> List.exists (fst >> PackageModules.samePath path) ->
                    let names = (chain |> List.rev |> List.map snd) @ [ found.Name ]
                    error DiagnosticCodes.PackageCycle dependency.NameSpan $"""packages can't depend on each other in a cycle: {String.concat " -> " names}"""
                | Result.Ok found ->
                    match reached.TryGetValue found.Name with
                    | true, other when not (PackageModules.samePath other path) ->
                        error
                            DiagnosticCodes.PackageReachedTwice
                            dependency.PathSpan
                            $"the build reaches the package {found.Name} by two paths, {Path.GetDirectoryName other} and {dependency.Directory}; a build has one of each package"
                    | _ ->
                        reached.[found.Name] <- path
                        ofManifestIn reached chain found
        manifest.Dependencies |> List.map read |> all

    /// The packages of a build whose root is `manifest`: none read yet but the root.
    let private reachedFrom (manifest: Manifest) : Reached =
        let reached = Reached()
        reached.[manifest.Name] <- manifest.Path
        reached

    /// The package whose manifest is at `manifestPath`, with the packages it depends on, or what is wrong with them.
    let ofManifest (manifestPath: string) : Result<Package, string list> =
        Manifest.read manifestPath
        |> Result.mapError (List.map Manifest.describe)
        |> Result.bind (fun manifest -> ofManifestIn (reachedFrom manifest) [] manifest |> Result.mapError (List.map describe))

    /// The manifest at `path`, whose text is `text`, as the editor shows it: a compile whose diagnostics are the problems
    /// with it (see `Manifest.check`), with the modules its `publicModules` names, and with the packages it depends on,
    /// nothing compiled, since a manifest is data.
    let compileManifest (path: string) (text: string) : CompileResult =
        let diagnostics =
            match Manifest.check path text with
            | Result.Error problems -> problems
            | Result.Ok manifest ->
                let dependencyErrors =
                    match dependenciesIn (reachedFrom manifest) [ manifest.Path, manifest.Name ] manifest with
                    | Result.Ok _ -> []
                    | Result.Error problems ->
                        problems
                        |> List.map (function
                            | AtManifest diagnostic -> diagnostic
                            | AboutPackage message -> Diagnostics.at DiagnosticCodes.InvalidManifestValue manifest.Value.Location message)
                publicModuleErrors manifest (sources manifest) @ dependencyErrors
        { Phase = Parsed
          Diagnostics = diagnostics
          Typed = None
          TypeChecked = false
          ErrorsDeferred = false
          Syntax = None
          ModuleName = None
          Lowered = Map.empty
          DocComments = lazy DocComments.none }

    /// `package` run under `host` in place of its manifest's, as `--host` chooses.
    let withHost (host: Host) (package: Package) : Package =
        { package with Manifest = { package.Manifest with Host = Some host } }

    /// The package `path` names: a manifest, or a directory in or under the package. An error says why there is none.
    let find (path: string) : Result<Package, string list> =
        let full = Path.GetFullPath path
        if File.Exists full && full.EndsWith(Manifest.extension, StringComparison.OrdinalIgnoreCase) then
            ofManifest full
        elif Directory.Exists full then
            match Manifest.tryFind full with
            | Result.Ok (Some manifest) -> ofManifest manifest
            | Result.Ok None -> Result.Error [ $"No {Manifest.extension} manifest in {full} or any directory above it" ]
            | Result.Error message -> Result.Error [ message ]
        else
            Result.Error [ $"Not a directory or a {Manifest.extension} manifest: {path}" ]

    /// The linter options for a file: those of the package manifest it is under, if it has one that reads.
    let lintersFor (filePath: string) : LinterOptions option =
        match Manifest.tryFind (Path.GetDirectoryName(Path.GetFullPath filePath)) with
        | Result.Ok (Some manifest) ->
            match Manifest.read manifest with
            | Result.Ok manifest -> Some manifest.Linters
            | Result.Error _ -> None
        | _ -> None

    /// An error for each `::` directive at the top of the file at `filePath`, whose text is `source`, which is in the
    /// package whose manifest is at `manifestPath`: directives say what a manifest would, for a script, which has none.
    let directiveErrors (manifestPath: string) (filePath: string) (source: string) : Diagnostic list =
        Directives.read (Path.GetFullPath filePath) source
        |> List.map (fun directive ->
            let name = if directive.Key.IsEmpty then "a :: line" else $"::{directive.Name}"
            Diagnostics.at
                DiagnosticCodes.DirectiveInPackage
                directive.Span
                $"{name} is a directive for a script, but this file is in a package: say it in the package's manifest, {manifestPath}")

    /// The modules of the package `manifest` describes, whose source files are `files`, with those of `dependencies`.
    let rec private modulesOf (manifest: Manifest) (files: string list) (dependencies: Package list) : PackageModules =
        { PackageModules.ofFiles manifest.Name manifest.Root manifest.Host files with
            PublicModules = manifest.PublicModules |> List.map fst
            Dependencies = dependencies |> List.map (fun dependency -> modulesOf dependency.Manifest dependency.Sources dependency.Dependencies) }

    /// The modules of `package`, by the names its source files declare, with those of the packages it depends on.
    let modules (package: Package) : PackageModules =
        modulesOf package.Manifest package.Sources package.Dependencies

    /// The modules of the package the file at `filePath` is a source file of, if it is one of a package whose
    /// manifest reads, with those of the packages it depends on, if they read too. A file the manifest selects that is
    /// not on disk yet (an editor's new buffer) is one of them.
    let modulesFor (filePath: string) : PackageModules option =
        let filePath = Path.GetFullPath filePath
        let directory = Path.GetDirectoryName filePath
        match if Directory.Exists directory then Manifest.tryFind directory else Result.Ok None with
        | Result.Ok (Some manifestPath) ->
            match Manifest.read manifestPath with
            | Result.Ok manifest when selects manifest filePath ->
                let dependencies =
                    match dependenciesIn (reachedFrom manifest) [ manifest.Path, manifest.Name ] manifest with
                    | Result.Ok dependencies -> dependencies
                    | Result.Error _ -> []
                let modules = modulesOf manifest (sources manifest) dependencies
                Some (if PackageModules.contains modules filePath then modules else { modules with Files = modules.Files @ [ filePath ] })
            | _ -> None
        | _ -> None

    /// `result`, the compile of the source file at `filePath` of the package `modules`, with the errors of a file that
    /// is not a named module of its own (see `PackageModules.fileErrors`).
    let private withFileErrors (modules: PackageModules) (filePath: string) (source: string) (result: CompileResult) : CompileResult =
        let compiled = result.Syntax |> Option.map (fun syntax -> result.ModuleName, syntax)
        match PackageModules.fileErrors modules (Path.GetFullPath filePath) source compiled with
        | [] -> result
        | errors -> { result with Diagnostics = errors @ result.Diagnostics; ErrorsDeferred = false }

    /// Compiles `source` as the source file at `filePath` of the package `modules`: its imports name the package's
    /// modules, and it must be a named module of its own (see `PackageModules.fileErrors`). Each module's check is
    /// reported to `listener`, and the package's other files it needs are read by `readFile`.
    let private compileIn (listener: CompileListener) (readFile: string -> string) (modules: PackageModules) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        Compiler.compileSourceInReporting listener readFile modules options filePath source |> withFileErrors modules filePath source

    /// Each of `files`, a source file of the package `modules` with its text, compiled as `compileIn` compiles one, but
    /// with each module checked once (#698). A module that checked, for one file or as an import of one, is not checked
    /// again for a later file that imports it, nor for its own file. One that failed to check is checked again where it
    /// is needed, since an import cycle is reported where the file being compiled enters it. `listener` is told each
    /// file's path as its compile starts, each module's check as it starts and ends, and each module already checked
    /// where it is needed again, its own file's included.
    let private compileAll (listener: CompileListener) (modules: PackageModules) (options: LinterOptions) (files: (string * string) list) : (string * CompileResult) list =
        let checkedOnce = Collections.Generic.Dictionary<string, CheckedModule>()
        Nanyx.LargeStack.run <| fun () ->
        files
        |> List.map (fun (file, source) ->
            listener.FileStarted file
            let origin = SourceFile (Path.GetFullPath file)
            let key = Compiler.originKey origin
            let checkedModules = Compiler.CheckedModules(Some modules, None, listener)
            for KeyValue(checkedKey, checkedModule) in checkedOnce do
                checkedModules.[checkedKey] <- Result.Ok checkedModule
            let checkedModule =
                match checkedOnce.TryGetValue key with
                | true, checkedModule ->
                    listener.ModuleReused key
                    Result.Ok checkedModule
                | false, _ -> Compiler.typecheckModule checkedModules origin source Set.empty
            for KeyValue(checkedKey, result) in Seq.append checkedModules [ Collections.Generic.KeyValuePair(key, checkedModule) ] do
                match result with
                | Result.Ok checked -> checkedOnce.TryAdd(checkedKey, checked) |> ignore
                | Result.Error _ -> ()
            file, Compiler.compileChecked checkedModules options origin source checkedModule |> withFileErrors modules file source)

    /// Compiles `source` as the file at `filePath` (which need not hold it yet: an editor's unsaved buffer, say), with
    /// the linters `options` gives: as a source file of the package it is in, if it is in one, or else on its own.
    /// Each module's check is reported to `listener`, and each other source file it needs is read by `readFile`, given
    /// the file's full path: an editor passes one that reads the text of a file it has open from its buffer.
    let compileSourceReading (listener: CompileListener) (readFile: string -> string) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        match modulesFor filePath with
        | Some modules -> compileIn listener readFile modules options filePath source
        | None -> Compiler.compileFromReporting listener readFile options (SourceFile (Path.GetFullPath filePath)) source

    /// `compileSourceReading`, with the other source files read from disk.
    let compileSourceReporting (listener: CompileListener) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileSourceReading listener File.ReadAllText options filePath source

    /// `compileSourceReporting`, telling nobody.
    let compileSourceWith (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileSourceReporting CompileListener.none options filePath source

    /// Checks every source file of `package`, running the linters its manifest turns on over each. `listener` is told
    /// each file's full path as its compile starts, in the order the files are compiled, and each module's check as it
    /// starts and ends, so a caller can show where a slow build is and how long each module took.
    let buildReporting (listener: CompileListener) (package: Package) : PackageBuild =
        let modules = modules package
        let sources = package.Sources |> List.map (fun file -> file, File.ReadAllText file)
        let results =
            compileAll listener modules package.Manifest.Linters sources
            |> List.map2 (fun (_, source) (file, result) ->
                let directives = directiveErrors package.Manifest.Path file source
                file,
                { result with
                    Diagnostics = directives @ result.Diagnostics
                    ErrorsDeferred = result.ErrorsDeferred && directives.IsEmpty }) sources
        // A file's errors are deferred when it did not type-check and each of its errors was placed; a linter rule
        // configured as an error, in a file that did type-check, is not deferred.
        let deferred (result: CompileResult) =
            not (result.Diagnostics |> List.exists Diagnostics.isError) || (not result.TypeChecked && result.ErrorsDeferred)
        { Package = package
          Results = results
          // A file's errors are reported once, though each file that imports it reports them too.
          Diagnostics = results |> List.collect (fun (_, result) -> result.Diagnostics |> List.map Diagnostics.inItsModule) |> List.distinct
          ErrorsDeferred = results |> List.forall (snd >> deferred) }

    /// `buildReporting`, telling nobody.
    let build (package: Package) : PackageBuild = buildReporting CompileListener.none package

    /// The compile of the package's `@entry` file, if it has one.
    let entryResult (build: PackageBuild) : CompileResult option =
        build.Package.Entry |> Option.bind (fun entry -> build.Results |> List.tryFind (fst >> (=) entry) |> Option.map snd)
