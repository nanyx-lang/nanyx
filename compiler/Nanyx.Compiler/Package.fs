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
      Dependencies: Package list
      /// The host its manifest's `host` names, read from the host's own manifest; none for a library.
      Host: Host option
      /// The host's package, whose modules the host's import resolves in.
      HostPackage: Package option }

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
        Manifest.unknownPublicModules manifest (PackageModules.ofFiles manifest.Name manifest.Root None files).Modules.Keys

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
        match entry, dependenciesIn reached ((manifest.Path, manifest.Name) :: chain) manifest.Dependencies, hostIn reached manifest.Host with
        | Result.Ok entry, Result.Ok dependencies, Result.Ok host when publicModules.IsEmpty ->
            Result.Ok
                { Manifest = manifest
                  Sources = files
                  Entry = entry
                  Dependencies = dependencies
                  Host = host |> Option.map fst
                  HostPackage = host |> Option.map snd }
        | entry, dependencies, host ->
            let errorsOf = function Result.Error errors -> errors | Result.Ok _ -> []
            Result.Error(errorsOf entry @ publicModules @ errorsOf dependencies @ errorsOf host)

    /// The host `entry` names, if there is one, read from the manifest of its package, which must declare it a host (see
    /// `HostPackages`), with that package: one the compiler ships, or one in the directory the entry gives.
    and private hostIn (reached: Reached) (entry: HostEntry option) : Result<(Host * Package) option, Problem list> =
        match entry with
        | None -> Result.Ok None
        | Some entry ->
            let error message = Result.Error [ AtManifest(Diagnostics.at DiagnosticCodes.InvalidManifestValue entry.Span message) ]
            let directory = entry.Path |> Option.defaultWith (fun () -> ShippedPackages.directoryOf entry.Package)
            match manifestIn directory with
            | Result.Error _ when entry.Path.IsNone -> error $"the compiler ships no package '{entry.Package}'"
            | Result.Error message -> error $"the host {entry.Package} can't be read: {message}"
            | Result.Ok path ->
                match Manifest.read path with
                | Result.Error errors -> Result.Error(errors |> List.map AtManifest)
                | Result.Ok found when found.Name <> entry.Package ->
                    error $"the package in {directory} is named {found.Name}, not {entry.Package}: the names must match"
                | Result.Ok found ->
                    match found.Declares with
                    | None -> error (HostPackages.notAHost entry.Package)
                    | Some host ->
                        reached.[found.Name] <- path
                        ofManifestIn reached [] found |> Result.map (fun package -> Some (host, package))

    /// The packages `dependencies` lists, each read with its own dependencies, or what is wrong: a directory with no
    /// manifest, a package named other than its dependant names it, one that depends on itself through others, or one
    /// reached by two paths. `chain` holds the manifest that lists them first, if one does.
    and private dependenciesIn (reached: Reached) (chain: (string * string) list) (dependencies: Dependency list) : Result<Package list, Problem list> =
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
        dependencies |> List.map read |> all

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
                let reached = reachedFrom manifest
                let dependencyErrors =
                    let host = match hostIn reached manifest.Host with Result.Ok _ -> [] | Result.Error problems -> problems
                    match dependenciesIn reached [ manifest.Path, manifest.Name ] manifest.Dependencies with
                    | Result.Ok _ when host.IsEmpty -> []
                    | Result.Ok _ -> host |> List.map (function AtManifest diagnostic -> diagnostic | AboutPackage message -> Diagnostics.at DiagnosticCodes.InvalidManifestValue manifest.Value.Location message)
                    | Result.Error problems ->
                        let problems = problems @ host
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

    /// `package` run under the host `name` names in place of its manifest's, as `--host` chooses: a directory, relative to
    /// `directory`, that holds a host's package, or the name of a host the compiler ships. An error says why it isn't one.
    let withHost (directory: string) (name: string) (package: Package) : Result<Package, string> =
        let full = Path.GetFullPath(name, directory)
        let entry =
            if Directory.Exists full then
                let packageName = match manifestIn full with Result.Ok path -> Path.GetFileNameWithoutExtension path | Result.Error _ -> name
                { Package = packageName; Path = Some full; Span = SourceSpan.none }
            else
                { Package = name; Path = None; Span = SourceSpan.none }
        match hostIn (Reached()) (Some entry) with
        | Result.Ok host -> Result.Ok { package with Host = host |> Option.map fst; HostPackage = host |> Option.map snd }
        | Result.Error problems -> Result.Error(problems |> List.map describe |> String.concat "; ")

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

    /// The modules of `package`, by the names its source files declare, with those of the packages it depends on and
    /// of its host's package.
    let rec modules (package: Package) : PackageModules =
        { PackageModules.ofFiles package.Manifest.Name package.Manifest.Root package.Host package.Sources with
            PublicModules = package.Manifest.PublicModules |> List.map fst
            Dependencies = package.Dependencies |> List.map modules
            HostPackage = package.HostPackage |> Option.map modules }

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
                let reached = reachedFrom manifest
                let dependencies =
                    match dependenciesIn reached [ manifest.Path, manifest.Name ] manifest.Dependencies with
                    | Result.Ok dependencies -> dependencies
                    | Result.Error _ -> []
                let host = match hostIn reached manifest.Host with Result.Ok host -> host | Result.Error _ -> None
                let modules =
                    modules
                        { Manifest = manifest
                          Sources = sources manifest
                          Entry = None
                          Dependencies = dependencies
                          Host = host |> Option.map fst
                          HostPackage = host |> Option.map snd }
                Some (if PackageModules.contains modules filePath then modules else { modules with Files = modules.Files @ [ filePath ] })
            | _ -> None
        | _ -> None

    /// The build of the script at `filePath`, which runs under the host `host` names, the console host the compiler ships
    /// if it names none, and imports from the packages `dependencies` lists, as its `::host` and `::dependencies`
    /// directives say (see `Script`); or what is wrong with them, each at the directive it is about, or at `span` for
    /// what is about a package as a whole.
    let scriptModules (filePath: string) (span: SourceSpan) (host: HostEntry option) (dependencies: Dependency list) : Result<PackageModules, Diagnostic list> =
        let host = host |> Option.defaultValue { Package = Hosts.consolePackage; Path = None; Span = span }
        let reached = Reached()
        let diagnostics problems =
            problems |> List.map (function AtManifest diagnostic -> diagnostic | AboutPackage message -> Diagnostics.at DiagnosticCodes.InvalidManifestValue span message)
        match hostIn reached (Some host), dependenciesIn reached [] dependencies with
        | Result.Ok (Some (host, hostPackage)), Result.Ok dependencies ->
            Result.Ok
                { PackageModules.ofFiles "" (Path.GetDirectoryName(Path.GetFullPath filePath)) (Some host) [] with
                    HostPackage = Some (modules hostPackage)
                    Dependencies = dependencies |> List.map modules
                    Script = true }
        | host, dependencies ->
            let errorsOf = function Result.Error errors -> errors | Result.Ok _ -> []
            Result.Error(diagnostics (errorsOf host @ errorsOf dependencies) |> List.distinct)

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
