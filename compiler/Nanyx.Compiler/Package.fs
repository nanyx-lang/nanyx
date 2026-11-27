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
      /// The files its manifest's `tests` select, as full paths in order: part of the package in a test build only (see
      /// `testBuild`).
      TestFiles: string list
      Entry: string option
      /// In the order its manifest lists them.
      Dependencies: Package list
      /// The files from GitHub its manifest lists, each a module of the package: its module name, and the full path of
      /// the file it is fetched to.
      ReferencedFiles: (string * string) list
      /// The host its manifest's `host` names, read from the host's own manifest; none for a library.
      Host: Host option
      /// The host's package, whose modules the host's import resolves in.
      HostPackage: Package option
      /// For a package in the package cache, the file its checked modules are kept in (see `CheckedPackages`).
      CheckedFile: string option
      /// In a test build (see `Package.withTests`), the test host its root runs under, with the host's package; none
      /// otherwise.
      TestHost: (Host * Package) option }

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

    /// The files the manifest's `tests` select, as full paths in order: none when it has no `tests`.
    let testFiles (manifest: Manifest) : string list =
        if manifest.Tests.IsEmpty then []
        else
            let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
            matcher.AddIncludePatterns manifest.Tests
            matcher.GetResultsInFullPath manifest.Root |> Seq.map Path.GetFullPath |> Seq.sort |> List.ofSeq

    /// Whether `patterns`, a manifest's `sources` or `tests`, select the file at the full path `filePath`, which need not
    /// exist yet.
    let private matches (manifest: Manifest) (patterns: string list) (filePath: string) =
        let relative = Path.GetRelativePath(manifest.Root, filePath)
        let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
        matcher.AddIncludePatterns patterns
        not (patterns.IsEmpty || Path.IsPathRooted relative || relative.StartsWith "..") && matcher.Match(relative).HasMatches

    /// Whether the file at the full path `filePath`, which need not exist yet, is a test file of the manifest's package:
    /// one its `tests` select and its `sources` don't.
    let private isTestFile (manifest: Manifest) (filePath: string) =
        matches manifest manifest.Tests filePath && not (matches manifest manifest.Sources filePath)

    /// Whether the manifest's `sources` or `tests` select the file at the full path `filePath`, which need not exist yet.
    let private selects (manifest: Manifest) (filePath: string) =
        matches manifest manifest.Sources filePath || matches manifest manifest.Tests filePath

    /// Whether the module at `path` declares an `@entry`. A file that does not parse does not; building it reports why.
    let private declaresEntry (path: string) =
        match parseModuleFile path (File.ReadAllText path) with
        | Result.Ok items -> items |> List.exists (function Kind (Attribute(name, _)) -> name = Attributes.entryAttribute | _ -> false)
        | Result.Error _ -> false

    /// The packages a build has read so far, by name, each with its manifest's path: a build has one of each.
    type private Reached = Collections.Generic.Dictionary<string, string>

    /// What is wrong with a package of a build: a diagnostic at the place in a manifest it is about, or one about the
    /// package as a whole, with its code.
    type private Problem =
        | AtManifest of Diagnostic
        | AboutPackage of DiagnosticCode * string

    let private describe (problem: Problem) =
        match problem with
        | AtManifest diagnostic -> Manifest.describe diagnostic
        | AboutPackage(code, message) -> Manifest.describe (Diagnostics.general code message)

    /// `problem` as a diagnostic, one about the package as a whole at `span`.
    let private diagnosticOf (span: SourceSpan) (problem: Problem) =
        match problem with
        | AtManifest diagnostic -> diagnostic
        | AboutPackage(code, message) -> Diagnostics.at code span message

    /// The errors of each of `results`, or the values of all of them.
    let private all (results: Result<'a, Problem list> list) : Result<'a list, Problem list> =
        match results |> List.collect (function Result.Error errors -> errors | Result.Ok _ -> []) with
        | [] -> Result.Ok(results |> List.choose (function Result.Ok value -> Some value | Result.Error _ -> None))
        | errors -> Result.Error(List.distinct errors)

    /// An error at the manifest's `package` if it is a library, with no `host` and no `@entry`, none of whose source
    /// files `files` exports its module with `export module`: another package could import nothing of it. An
    /// application needn't export anything, nor need a package with an `@entry` run with `--host`; one with an
    /// `@entry` and no host at all is an error of its own (`EntryInLibrary`).
    let private exportErrors (manifest: Manifest) (files: string list) : Diagnostic list =
        let isLibrary = manifest.Host.IsNone && not (files |> List.exists declaresEntry)
        // A package with no source files is an error of its own (`NoSourceFiles`).
        if files.IsEmpty || not isLibrary || not (PackageModules.ofFiles manifest.Name manifest.Root None files).PublicModules.IsEmpty then
            []
        else
            [ Diagnostics.at
                  DiagnosticCodes.LibraryExportsNothing
                  manifest.PackageSpan
                  $"'{manifest.Name}' is a library, since its manifest names no host, but it exports no module: export at least one, by declaring it with `export module`"
              |> Diagnostics.about manifest.Name ]

    /// The problem with the GitHub reference `what`, written at `span`, that `failure` kept from being fetched.
    let private notFetched (span: SourceSpan) (what: string) (failure: FetchFailure) : Problem =
        match failure with
        | FetchingNotSupported -> AtManifest(Diagnostics.at DiagnosticCodes.UnsupportedManifestField span $"{what} is on GitHub, but {GitHub.describe failure}")
        | NotPulled -> AtManifest(Diagnostics.at DiagnosticCodes.PackageNotDownloaded span $"{what} is on GitHub, but {GitHub.describe failure}")
        | FetchFailed message -> AtManifest(Diagnostics.at DiagnosticCodes.InvalidManifestValue span $"{what} can't be fetched from GitHub: {message}")

    /// The files from GitHub `references` lists, fetched by `fetcher`, each with the module name its entry gives it, or
    /// what is wrong: one that can't be fetched, one that declares a module name of its own, which it can't (the entry
    /// names it, and nothing can export it), and one named for a module of `own`, the package's own modules.
    let private referencedFiles (fetcher: GitHubFetcher) (own: Map<string, string list>) (references: FileReference list) : Result<(string * string) list, Problem list> =
        references
        |> List.map (fun file ->
            let reference = file.Reference
            match fetcher.FetchFile reference.Repository reference.Commit (defaultArg reference.File "") with
            | Result.Error failure -> Result.Error [ notFetched file.GitHubSpan $"The module '{file.Module}'" failure ]
            | Result.Ok path ->
                match PackageModules.declaredModule path with
                | Some (declared, _) ->
                    Result.Error
                        [ AtManifest(
                              Diagnostics.at
                                  DiagnosticCodes.InvalidReferencedFile
                                  file.Span
                                  $"'{reference}' declares the module name '{declared}', but a file from GitHub declares none, since the manifest names it '{file.Module}' and no package can export it: use a file that declares no module name"
                              |> Diagnostics.about (string reference)
                          ) ]
                | None when own.ContainsKey file.Module ->
                    Result.Error
                        [ AtManifest(
                              Diagnostics.at
                                  DiagnosticCodes.DuplicateModule
                                  file.Span
                                  $"The package's own files declare the module '{file.Module}' already: give the file from GitHub another name"
                              |> Diagnostics.about file.Module
                          ) ]
                | None -> Result.Ok(file.Module, path))
        |> all

    /// What a build reads the packages it depends on from: the store, and the lockfile of the package it builds, which
    /// every package of the build shares, since a build has one version of each package. A script's is the versions its
    /// directives pin (see `ScriptDependencies.cached`).
    type private Lookup = { Store: PackageStore; Locked: Lockfile }

    /// The versions compatible with this compiler's, at which a host the compiler ships is named where no manifest names
    /// it: by `--host`, or as a script's host.
    let private atCompilerVersion = VersionRequirement.at (SemanticVersion.tryParse Toolchain.version |> Option.get)

    /// What a manifest asks for a package from the index as: a dependency, or its host.
    type private Requested =
        | ADependency
        | AHost

    /// Where a package from the index is read from: the cache, which never changes, so its checked modules can be kept
    /// beside it; or the compiler's own copy of a package it seeded into the cache, whose checked modules it has already
    /// (see `PackageStore.seededCopy`).
    type private Found =
        | InCache of directory: string
        | Seeded of copy: SeededCopy

    /// The directory a found package is read from.
    let private directoryOf (found: Found) =
        match found with
        | InCache directory -> directory
        | Seeded copy -> copy.Source

    /// Where the package `name` from the index, which a manifest asks for at `span` at the versions `requirement` accepts,
    /// is read from: the version the lockfile locks, from the compiler's own copy if the compiler seeded that version
    /// into the cache, or else from the cache. One the lockfile doesn't lock yet is read from the copy the compiler
    /// seeded, if `requirement` accepts its version, so that a package needs no `nanyx pull` to build with the packages
    /// the compiler ships, and the next one locks it. Or why it can't be read: it isn't locked, the version locked isn't
    /// one it accepts, or that version isn't in the cache, which `nanyx pull` fills.
    let private locked (lookup: Lookup) (what: Requested) (name: string) (span: SourceSpan) (requirement: VersionRequirement) : Result<Found, Problem list> =
        let error code message = Result.Error [ AtManifest(Diagnostics.at code span message |> Diagnostics.about name) ]
        let seeded = PackageStore.seededCopy lookup.Store name
        match Lockfile.tryFind name lookup.Locked with
        | None ->
            match seeded with
            | Some copy when VersionRequirement.accepts requirement copy.Version -> Result.Ok(Seeded copy)
            | _ ->
                match what with
                | ADependency ->
                    error
                        DiagnosticCodes.DependencyNotLocked
                        $"The dependency '{name}' {requirement} is not locked in '{Lockfile.fileName}': run `nanyx pull` to choose its version and lock it"
                | AHost ->
                    error
                        DiagnosticCodes.DependencyNotLocked
                        $"The host '{name}' {requirement} is neither one the compiler ships nor locked in '{Lockfile.fileName}': run `nanyx pull` to choose its version from the package index and lock it, or give the directory it is in, as in `host = (package = \"{name}\", path = \"../{name}\")`"
        | Some locked when not (VersionRequirement.accepts requirement locked.Version) ->
            error
                DiagnosticCodes.InvalidManifestValue
                $"'{Lockfile.fileName}' locks '{name}' {locked.Version}, but this dependency accepts only {requirement} and the versions compatible with it"
        | Some locked ->
            let directory = PackageStore.directoryOf lookup.Store name locked.Version
            match seeded with
            | Some copy when copy.Version = locked.Version -> Result.Ok(Seeded copy)
            | _ when Directory.Exists directory -> Result.Ok(InCache directory)
            | _ ->
                error
                    DiagnosticCodes.PackageNotDownloaded
                    $"'{Lockfile.fileName}' locks '{name}' {locked.Version}, but it isn't in the package cache, '{lookup.Store.Cache}': run `nanyx pull` to download it"

    /// The package `manifest` describes, with the packages it depends on, found through `lookup`. `chain` is the manifests
    /// of the packages that led to it, nearest first, each with its package's name, and `reached` every package the
    /// build has read so far.
    let rec private ofManifestIn (lookup: Lookup) (reached: Reached) (chain: (string * string) list) (manifest: Manifest) : Result<Package, Problem list> =
        let files = sources manifest
        let tests = testFiles manifest
        let display (file: string) = Path.GetRelativePath(manifest.Root, file)
        // A test file is part of the package in a test build only, so it can't be one of its sources too.
        let testsAmongSources =
            match tests |> List.filter (fun file -> files |> List.contains file) with
            | [] -> []
            | both ->
                [ AboutPackage(DiagnosticCodes.InvalidManifestValue, $"""'{manifest.Path}' selects {both |> List.map (display >> sprintf "'%s'") |> Diagnostics.listed} with both `sources` and `tests`, but a test file is part of the package only in a test build: select it with one of them""") ]
        let entry =
            // A test package has no sources, and so no `@entry`.
            if Manifest.isTestPackage manifest then Result.Ok None
            elif manifest.Sources.IsEmpty then
                Result.Error [ AboutPackage(DiagnosticCodes.NoSourceFiles, $"'{manifest.Path}' selects no sources and no tests: select its sources with `sources`, or its test files with `tests`") ]
            elif files.IsEmpty then
                Result.Error [ AboutPackage(DiagnosticCodes.NoSourceFiles, $"""'{manifest.Path}' selects its sources with {manifest.Sources |> List.map (sprintf "'%s'") |> Diagnostics.listed}, which match no file""") ]
            else
                match files |> List.filter declaresEntry with
                | [] -> Result.Ok None
                | [ entry ] -> Result.Ok(Some entry)
                | several ->
                    Result.Error [ AboutPackage(DiagnosticCodes.SeveralEntryPoints, $"""More than one file of '{manifest.Path}' declares an `@entry`, {several |> List.map (display >> sprintf "'%s'") |> Diagnostics.listed}, but a package has one: keep one of them""") ]
        let exports = exportErrors manifest files |> List.map AtManifest
        let referenced = referencedFiles lookup.Store.GitHub (PackageModules.ofFiles manifest.Name manifest.Root None files).Modules manifest.Files
        match entry, dependenciesIn lookup reached ((manifest.Path, manifest.Name) :: chain) manifest.Dependencies, hostIn lookup reached manifest.Host with
        | Result.Ok entry, Result.Ok dependencies, Result.Ok host when exports.IsEmpty && referenced.IsOk && testsAmongSources.IsEmpty ->
            Result.Ok
                { Manifest = manifest
                  Sources = files
                  TestFiles = tests
                  Entry = entry
                  Dependencies = dependencies
                  ReferencedFiles = Result.defaultValue [] referenced
                  Host = host |> Option.map fst
                  HostPackage = host |> Option.map snd
                  CheckedFile = None
                  TestHost = None }
        | entry, dependencies, host ->
            let errorsOf = function Result.Error errors -> errors | Result.Ok _ -> []
            Result.Error(errorsOf entry @ exports @ testsAmongSources @ errorsOf referenced @ errorsOf dependencies @ errorsOf host)

    /// The host `entry` names, if there is one, read from the manifest of its package, which must declare it a host (see
    /// `HostPackages`), with that package: one from the index, found as a dependency from the index is (see `locked`), or
    /// one in the directory the entry gives.
    and private hostIn (lookup: Lookup) (reached: Reached) (entry: HostEntry option) : Result<(Host * Package) option, Problem list> =
        match entry with
        | None -> Result.Ok None
        | Some entry ->
            let error message = Result.Error [ AtManifest(Diagnostics.at DiagnosticCodes.InvalidManifestValue entry.Span message) ]
            let directory =
                match entry.Path, entry.Version with
                | Some directory, _ -> Result.Ok directory
                | None, Some requirement -> locked lookup AHost entry.Package entry.Span requirement |> Result.map directoryOf
                | None, None -> error $"The host '{entry.Package}' has no `version` or `path`"
            match directory |> Result.map (fun directory -> directory, Manifest.manifestIn directory) with
            | Result.Error problems -> Result.Error problems
            | Result.Ok(_, Result.Error message) -> error $"The host '{entry.Package}' can't be read: {message}"
            | Result.Ok(directory, Result.Ok path) ->
                match Manifest.read path with
                | Result.Error errors -> Result.Error(errors |> List.map AtManifest)
                | Result.Ok found when found.Name <> entry.Package ->
                    error $"The package in '{directory}' is named '{found.Name}', not '{entry.Package}': make the names match"
                | Result.Ok found ->
                    match found.Declares with
                    | None -> error (HostPackages.notAHost entry.Package)
                    | Some host ->
                        match HostSettings.errors host entry with
                        | [] ->
                            reached.[found.Name] <- path
                            ofManifestIn lookup reached [] found |> Result.map (fun package -> Some (host, package))
                        | errors -> Result.Error(errors |> List.map AtManifest)

    /// The packages `dependencies` lists, each read with its own dependencies, or what is wrong: a directory with no
    /// manifest, a package named other than its dependant names it, one that depends on itself through others, or one
    /// reached by two paths. One on GitHub is fetched first, and one named by its version is the version the lockfile
    /// locks, unpacked in the cache. `chain` holds the manifest that lists them first, if one does.
    and private dependenciesIn (lookup: Lookup) (reached: Reached) (chain: (string * string) list) (dependencies: Dependency list) : Result<Package list, Problem list> =
        let error code span message = Result.Error [ AtManifest(Diagnostics.at code span message) ]
        let fetched (dependency: Dependency) =
            match dependency.Source with
            | OnGitHub reference ->
                lookup.Store.GitHub.FetchTree reference.Repository reference.Commit
                |> Result.mapError (fun failure -> [ notFetched dependency.SourceSpan $"The dependency '{dependency.Name}'" failure ])
            | FromIndex requirement -> locked lookup ADependency dependency.Name dependency.SourceSpan requirement |> Result.map directoryOf
            | AtPath directory -> Result.Ok directory
        let read (dependency: Dependency) (directory: string) =
            match Manifest.manifestIn directory with
            | Result.Error message -> error DiagnosticCodes.InvalidManifestValue dependency.SourceSpan $"The dependency '{dependency.Name}' can't be read: {message}"
            | Result.Ok path ->
                match Manifest.read path with
                | Result.Error errors -> Result.Error(errors |> List.map AtManifest)
                | Result.Ok found when found.Name <> dependency.Name ->
                    error
                        DiagnosticCodes.InvalidManifestValue
                        dependency.NameSpan
                        $"The package in '{directory}' is named '{found.Name}', not '{dependency.Name}': make the names match"
                | Result.Ok found when chain |> List.exists (fst >> PackageModules.samePath path) ->
                    let names = (chain |> List.rev |> List.map snd) @ [ found.Name ]
                    error DiagnosticCodes.PackageCycle dependency.NameSpan $"""Packages can't depend on each other in a cycle: {names |> List.map (sprintf "'%s'") |> String.concat " -> "}"""
                | Result.Ok found ->
                    match reached.TryGetValue found.Name with
                    | true, other when not (PackageModules.samePath other path) ->
                        let message =
                            $"The build reaches the package '{found.Name}' by two paths, '{Path.GetDirectoryName other}' and '{directory}', but a build has one of each package"
                        Result.Error [ AtManifest(Diagnostics.at DiagnosticCodes.PackageReachedTwice dependency.SourceSpan message |> Diagnostics.about found.Name) ]
                    | _ ->
                        reached.[found.Name] <- path
                        ofManifestIn lookup reached chain found
        // A package from the index or GitHub is in the cache, and never changes, so its checked modules can be kept; the
        // compiler's own copy of one it seeded is checked already (see `CheckedLibrary`).
        let checkedFile (dependency: Dependency) (package: Package) =
            let kept identity = { package with CheckedFile = Some(CheckedPackages.fileFor lookup.Store.Cache package.Manifest.Name identity) }
            match dependency.Source with
            | FromIndex _ when PackageStore.seededCopy lookup.Store dependency.Name |> Option.exists (fun copy -> PackageModules.samePath copy.Source package.Manifest.Root) ->
                package
            | FromIndex _ -> kept (string package.Manifest.Version)
            | OnGitHub reference -> kept reference.Commit
            | AtPath _ -> package
        dependencies
        |> List.map (fun dependency -> fetched dependency |> Result.bind (read dependency) |> Result.map (checkedFile dependency))
        |> all

    /// The packages of a build whose root is `manifest`: none read yet but the root.
    let private reachedFrom (manifest: Manifest) : Reached =
        let reached = Reached()
        reached.[manifest.Name] <- manifest.Path
        reached

    /// What the build of the package whose manifest is `manifest` reads packages from: `store`, and the lockfile beside
    /// the manifest, if it has one; or what is wrong with the lockfile.
    let private lookupFor (store: PackageStore) (manifest: Manifest) : Result<Lookup, string list> =
        Lockfile.read (Lockfile.pathIn manifest.Root)
        |> Result.map (fun locked -> { Store = store; Locked = defaultArg locked Lockfile.empty })

    /// The package whose manifest is at `manifestPath`, with the packages it depends on, found in `store` and its
    /// lockfile, or what is wrong with them.
    let ofManifestFrom (store: PackageStore) (manifestPath: string) : Result<Package, string list> =
        Manifest.read manifestPath
        |> Result.mapError (List.map Manifest.describe)
        |> Result.bind (fun manifest ->
            lookupFor store manifest
            |> Result.bind (fun lookup -> ofManifestIn lookup (reachedFrom manifest) [] manifest |> Result.mapError (List.map describe)))

    /// A compile of nothing, with no diagnostics: what the compile of a file that can't be compiled has, with its errors.
    let private notCompiled : CompileResult =
        { Phase = Parsed
          Diagnostics = []
          Typed = None
          TypeChecked = false
          ErrorsDeferred = false
          Syntax = None
          ModuleName = None
          Lowered = Map.empty
          DocComments = lazy DocComments.none }

    /// The manifest at `path`, whose text is `text`, as the editor shows it: a compile whose diagnostics are the problems
    /// with it (see `Manifest.checkPinning`), with a library that exports no module, and with the packages and files it
    /// depends on, found in `store` and the lockfile beside it; nothing compiled, since a manifest is data. A GitHub
    /// reference that is not pinned to a commit has a fix that pins it to the one the store finds its tag or branch
    /// points at.
    let compileManifestFrom (store: PackageStore) (path: string) (text: string) : CompileResult =
        let pin repository rev = store.GitHub.Resolve repository rev |> Result.toOption
        let diagnostics =
            match Manifest.checkPinning pin path text with
            | Result.Error problems -> problems
            | Result.Ok manifest ->
                let reached = reachedFrom manifest
                let errorsOf = function Result.Error problems -> problems | Result.Ok _ -> []
                let lookup, lockfileErrors =
                    match lookupFor store manifest with
                    | Result.Ok lookup -> lookup, []
                    | Result.Error messages -> { Store = store; Locked = Lockfile.empty }, messages |> List.map (fun message -> AboutPackage(DiagnosticCodes.InvalidLockfile, message))
                let problems =
                    lockfileErrors
                    @ errorsOf (referencedFiles store.GitHub (PackageModules.ofFiles manifest.Name manifest.Root None (sources manifest)).Modules manifest.Files)
                    @ errorsOf (dependenciesIn lookup reached [ manifest.Path, manifest.Name ] manifest.Dependencies)
                    @ errorsOf (hostIn lookup reached manifest.Host)
                let dependencyErrors = problems |> List.map (diagnosticOf manifest.Value.Location)
                exportErrors manifest (sources manifest) @ dependencyErrors
        { notCompiled with Diagnostics = diagnostics }

    /// `package` run under the host `name` names in place of its manifest's, as `--host` chooses: a directory, relative to
    /// `directory`, that holds a host's package, or the name of a host the compiler ships, found in `store`. An error says
    /// why it isn't one.
    let withHost (store: PackageStore) (directory: string) (name: string) (package: Package) : Result<Package, string> =
        let full = Path.GetFullPath(name, directory)
        let entry =
            if Directory.Exists full then
                let packageName = match Manifest.manifestIn full with Result.Ok path -> Path.GetFileNameWithoutExtension path | Result.Error _ -> name
                { Package = packageName; Path = Some full; Version = None; Span = SourceSpan.none; Settings = [] }
            else
                { Package = name; Path = None; Version = Some atCompilerVersion; Span = SourceSpan.none; Settings = [] }
        // A host named by its old name is told its new one (see `RenamedPackages`).
        match RenamedPackages.asHost name with
        | Some renamed when entry.Path.IsNone -> Result.Error renamed
        | _ ->
            match hostIn { Store = store; Locked = Lockfile.empty } (Reached()) (Some entry) with
            | Result.Ok host -> Result.Ok { package with Host = host |> Option.map fst; HostPackage = host |> Option.map snd }
            | Result.Error problems -> Result.Error(problems |> List.map describe |> String.concat "; ")

    /// The package `path` names: a manifest, or a directory in or under the package, with the packages it depends on,
    /// found in `store`. An error says why there is none.
    let findIn (store: PackageStore) (path: string) : Result<Package, string list> =
        let full = Path.GetFullPath path
        if File.Exists full && full.EndsWith(Manifest.extension, StringComparison.OrdinalIgnoreCase) then
            ofManifestFrom store full
        elif Directory.Exists full then
            match Manifest.tryFind full with
            | Result.Ok (Some manifest) -> ofManifestFrom store manifest
            | Result.Ok None -> Result.Error [ $"There is no {Manifest.extension} manifest in '{full}' or any directory above it" ]
            | Result.Error message -> Result.Error [ message ]
        else
            Result.Error [ $"'{path}' is neither a directory nor a {Manifest.extension} manifest" ]

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
            let name = if directive.Name = "" then "A `::` line" else $"`::{directive.Name}`"
            Diagnostics.at
                DiagnosticCodes.DirectiveInPackage
                directive.Span
                $"{name} is a directive for a script, but this file is in a package: say it in the package's manifest, '{manifestPath}'")

    /// The name of the file a test build's root is compiled as, in the package's directory or the script's: no file of
    /// theirs.
    let testRootFile = "__tests.nyx"

    /// The full path of the root a test build of the package `manifest` describes is compiled as (see `testResult`): no
    /// file of the package's, but compiled as if it were in its directory.
    let testRootIn (manifest: Manifest) = Path.Combine(manifest.Root, testRootFile)

    /// The modules of `package`, by the names its source files declare, with those of the packages it depends on and
    /// of its host's package.
    let rec modules (package: Package) : PackageModules =
        { PackageModules.ofFiles package.Manifest.Name package.Manifest.Root package.Host package.Sources with
            Dependencies = package.Dependencies |> List.map modules
            HostPackage = package.HostPackage |> Option.map modules
            CheckedFile = package.CheckedFile
            HostLibrary = package.Manifest.HostLibrary
            DeclaresHost = package.Manifest.Declares.IsSome
            TestRoot = package.TestHost |> Option.map (fun _ -> testRootIn package.Manifest)
            TestHost = package.TestHost |> Option.map (fun (host, hostPackage) -> host, modules hostPackage) }
        |> PackageModules.withReferenced package.ReferencedFiles

    /// `package` as a test build has it, with the test host found in `store`, or why the test host can't be read: its
    /// test files among its sources, so that they are compiled, and that its modules and theirs can import each other,
    /// each of the program's modules giving the build's root a way to run its tests (see `PackageModules.TestRoot`).
    /// The program's modules keep its own host, whose prelude they see, and only the root runs under the test host, but
    /// for a library's, which has none.
    let withTests (store: PackageStore) (package: Package) : Result<Package, string> =
        withHost store package.Manifest.Root Hosts.testPackage package
        |> Result.map (fun underTest ->
            { package with
                Sources = package.Sources @ package.TestFiles
                // A library has no host, nor a prelude of its own, so its modules run under the test host, and see its.
                Host = package.Host |> Option.orElse underTest.Host
                HostPackage = package.HostPackage |> Option.orElse underTest.HostPackage
                TestHost = Option.map2 (fun host hostPackage -> host, hostPackage) underTest.Host underTest.HostPackage })

    /// The manifest of the package the file at `filePath` is in, if it is under one that reads, with the file's full path.
    let private manifestOver (filePath: string) : (Manifest * string) option =
        let filePath = Path.GetFullPath filePath
        let directory = Path.GetDirectoryName filePath
        match if Directory.Exists directory then Manifest.tryFind directory else Result.Ok None with
        | Result.Ok(Some manifestPath) -> Manifest.read manifestPath |> Result.toOption |> Option.map (fun manifest -> manifest, filePath)
        | _ -> None

    /// The name of the package the file at `filePath` is a source file or a test file of, if it is in one whose manifest
    /// reads.
    let nameFor (filePath: string) : string option =
        match manifestOver filePath with
        | Some (manifest, filePath) when selects manifest filePath -> Some manifest.Name
        | _ -> None

    /// The manifest of the package the file at `filePath` is a test file of, if it is one: its `tests` select the file,
    /// and its `sources` don't. Only a test build has such a file, so `nanyx test` runs its tests as part of the package.
    let testFileOf (filePath: string) : Manifest option =
        match manifestOver filePath with
        | Some (manifest, filePath) when isTestFile manifest filePath -> Some manifest
        | _ -> None

    /// The modules of the package the file at `filePath` is a source file or a test file of, if it is one, with those of
    /// the packages it depends on, found in `store`, if they read; none for a file in no package. A test file's are a test
    /// build's (see `withTests`), so it imports the package's modules, and an application the package depends on keeps
    /// its host and its `@entry` is an ordinary def; a source file's include the test files, so that an editor finds a
    /// name's uses in them too. A file the manifest selects that is not on disk yet (an editor's new buffer) is one of
    /// them. The manifest's problems are the error if it does not read: which files it selects can't be known, and the
    /// file can't be checked as a script without blaming its package imports.
    let modulesFor (store: PackageStore) (filePath: string) : Result<PackageModules option, Diagnostic list> =
        let filePath = Path.GetFullPath filePath
        let directory = Path.GetDirectoryName filePath
        match if Directory.Exists directory then Manifest.tryFind directory else Result.Ok None with
        | Result.Ok (Some manifestPath) ->
            match Manifest.read manifestPath with
            | Result.Error problems -> Result.Error problems
            | Result.Ok manifest when selects manifest filePath ->
                let reached = reachedFrom manifest
                let lookup = lookupFor store manifest |> Result.defaultValue { Store = store; Locked = Lockfile.empty }
                let dependencies =
                    match dependenciesIn lookup reached [ manifest.Path, manifest.Name ] manifest.Dependencies with
                    | Result.Ok dependencies -> dependencies
                    | Result.Error _ -> []
                let host = match hostIn lookup reached manifest.Host with Result.Ok host -> host | Result.Error _ -> None
                let sources = sources manifest
                let tests = testFiles manifest
                let package =
                    { Manifest = manifest
                      Sources = sources
                      TestFiles = tests
                      Entry = None
                      Dependencies = dependencies
                      ReferencedFiles =
                        referencedFiles store.GitHub (PackageModules.ofFiles manifest.Name manifest.Root None (sources @ tests)).Modules manifest.Files
                        |> Result.defaultValue []
                      Host = host |> Option.map fst
                      HostPackage = host |> Option.map snd
                      CheckedFile = None
                      TestHost = None }
                // An editor compiles a test file as a test build does, and a source file with the test files among the
                // package's, but as its other builds do.
                let package =
                    if isTestFile manifest filePath then withTests store package |> Result.defaultValue { package with Sources = sources @ tests }
                    else { package with Sources = sources @ tests }
                // There is no root, so the file gets no `Attributes.testDispatcher`: a def the editor would show, declared
                // where the file's first test is.
                let modules = { modules package with TestRoot = None }
                Result.Ok(Some (if PackageModules.contains modules filePath then modules else { modules with Files = modules.Files @ [ filePath ] }))
            | Result.Ok _ -> Result.Ok None
        | _ -> Result.Ok None

    /// The build of the script at `filePath`, which runs under the host `host` names, the host the compiler ships as the
    /// package `defaultHost` if it names none, and imports from the packages `dependencies` lists and the files from GitHub `files` lists, as
    /// its `::host` and `::install` directives say (see `Script`), found in `store`, at the versions `locked` gives
    /// those named by their version; or what is wrong with them, each at the directive it is about, or at `span` for
    /// what is about a package as a whole.
    let scriptModules
        (defaultHost: string)
        (store: PackageStore)
        (locked: Lockfile)
        (filePath: string)
        (span: SourceSpan)
        (host: HostEntry option)
        (dependencies: Dependency list)
        (files: FileReference list)
        : Result<PackageModules, Diagnostic list> =
        let host = host |> Option.defaultValue { Package = defaultHost; Path = None; Version = Some atCompilerVersion; Span = span; Settings = [] }
        let reached = Reached()
        let lookup = { Store = store; Locked = locked }
        let diagnostics problems =
            problems |> List.map (diagnosticOf span)
        match hostIn lookup reached (Some host), dependenciesIn lookup reached [] dependencies, referencedFiles store.GitHub Map.empty files with
        | Result.Ok (Some (host, hostPackage)), Result.Ok dependencies, Result.Ok referenced ->
            Result.Ok(
                { PackageModules.ofFiles "" (Path.GetDirectoryName(Path.GetFullPath filePath)) (Some host) [] with
                    HostPackage = Some (modules hostPackage)
                    Dependencies = dependencies |> List.map modules
                    Script = true }
                |> PackageModules.withReferenced referenced
            )
        | host, dependencies, referenced ->
            let errorsOf = function Result.Error errors -> errors | Result.Ok _ -> []
            Result.Error(diagnostics (errorsOf host @ errorsOf dependencies @ errorsOf referenced) |> List.distinct)

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
    /// where it is needed again, its own file's included. A file that is not among `files` is read by `readFile`.
    let private compileAll (listener: CompileListener) (readFile: string -> string) (modules: PackageModules) (options: LinterOptions) (files: (string * string) list) : (string * CompileResult) list =
        let checkedOnce = Collections.Generic.Dictionary<string, CheckedModule>()
        Nanyx.LargeStack.run <| fun () ->
        files
        |> List.map (fun (file, source) ->
            listener.FileStarted file
            let origin = SourceFile (Path.GetFullPath file)
            let key = ModuleOrigins.originKey origin
            let checkedModules = Compiler.CheckedModules(Some modules, None, listener, readFile)
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
    /// the file's full path: an editor passes one that reads the text of a file it has open from its buffer. A file
    /// under a manifest that does not read is not compiled: the manifest's problems are its errors. The packages it depends
    /// on are found in `store`.
    let compileSourceReading (store: PackageStore) (listener: CompileListener) (readFile: string -> string) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        match modulesFor store filePath with
        | Result.Ok (Some modules) -> compileIn listener readFile modules options filePath source
        | Result.Ok None -> Compiler.compileFromReporting listener readFile options (SourceFile (Path.GetFullPath filePath)) source
        | Result.Error problems -> { notCompiled with Diagnostics = problems }

    /// Every source file of the package the file at `filePath` is a source file of, each compiled as `buildReporting`
    /// compiles it but with no linters, and each read by `readFile`, given its full path, as `compileSourceReading`
    /// reads them; what an editor searches for the uses of a name. The module each file declares is read from its text
    /// too, so a renamed `module` line is the name its imports find. `None` for a file in no package, or under a
    /// manifest that does not read.
    let compilePackageReading (store: PackageStore) (readFile: string -> string) (filePath: string) : (string * CompileResult) list option =
        match modulesFor store filePath with
        | Result.Ok (Some modules) ->
            let modules = PackageModules.reading readFile modules
            let sources = modules.Files |> List.map (fun file -> file, readFile file)
            Some (compileAll CompileListener.none readFile modules Linters.none sources)
        | _ -> None

    /// The full path of every source file of the package the file at `filePath` is a source file of, as
    /// `compilePackageReading` compiles them, without compiling them. `None` as for `compilePackageReading`.
    let sourceFilesOf (store: PackageStore) (filePath: string) : string list option =
        match modulesFor store filePath with
        | Result.Ok (Some modules) -> Some (modules.Files |> List.map Path.GetFullPath)
        | _ -> None

    /// `compileSourceReading`, with the other source files read from disk.
    let compileSourceReporting (store: PackageStore) (listener: CompileListener) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileSourceReading store listener File.ReadAllText options filePath source

    /// `compileSourceReporting`, telling nobody.
    let compileSourceWith (store: PackageStore) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileSourceReporting store CompileListener.none options filePath source

    /// Checks every source file of `package`, running the linters its manifest turns on over each. `listener` is told
    /// each file's full path as its compile starts, in the order the files are compiled, and each module's check as it
    /// starts and ends, so a caller can show where a slow build is and how long each module took.
    let buildReporting (listener: CompileListener) (package: Package) : PackageBuild =
        let modules = modules package
        let sources = package.Sources |> List.map (fun file -> file, File.ReadAllText file)
        let results =
            compileAll listener File.ReadAllText modules package.Manifest.Linters sources
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

    /// Checks every source file and every test file of `package` (see `withTests`), as `build` checks its sources; or
    /// why the test host, found in `store`, can't be read.
    let testBuild (store: PackageStore) (package: Package) : Result<PackageBuild, string> =
        withTests store package |> Result.map build

    /// The modules of the package `build` built that mark a def `@test`, by name, in the order of its files, each with
    /// how many it marks.
    let testModules (build: PackageBuild) : (string * int) list =
        build.Results
        |> List.choose (fun (_, result) ->
            match result.ModuleName, result.Typed with
            | Some name, Some typed ->
                match typed.Tests |> List.filter (fun test -> test.Module = name) with
                | [] -> None
                | own -> Some (name, own.Length)
            | _ -> None)
        |> List.distinctBy fst

    /// The source of the root a test build is compiled from: a module that imports each of `modules`, each as an import
    /// writes it, a module's name or a file's path in quotes, with how many tests it has, and whose `@entry` runs the test
    /// at the index the test host gives it, counting through each module's tests in turn, by calling its
    /// `Attributes.testDispatcher`.
    let testRootSource (modules: (string * int) list) =
        let imports = modules |> List.mapi (fun index (imported, _) -> $"import {imported} as {TestRootSyntax.alias index}\n") |> String.concat ""
        let starts = modules |> List.scan (fun start (_, count) -> start + count) 0
        let arms =
            modules
            |> List.mapi (fun index (_, count) ->
                let start = starts.[index]
                $"if index < {start + count} then {TestRootSyntax.alias index}.{Attributes.testDispatcher}(index - {start}) else ")
            |> String.concat ""
        $"{imports}\n@entry\ndef run = {{ index -> {arms}() }}\n"

    /// The compile of the root a test build of the package `build` built (see `testBuild`) is compiled from, if its
    /// modules have tests: no file of the package's, compiled as `testRootIn` from `testRootSource`. Its `Typed.Tests`
    /// are every test of the package, in the order its `@entry` counts them.
    let testResult (build: PackageBuild) : CompileResult option =
        match testModules build with
        | [] -> None
        | counted ->
            let path = testRootIn build.Package.Manifest
            // It is compiled as a file of the package, so its imports name the package's modules.
            let packageModules = modules build.Package
            let packageModules = { packageModules with Files = packageModules.Files @ [ path ] }
            let result = Compiler.compileSourceIn packageModules Linters.none path (testRootSource counted)
            let inOrder (tests: Test list) = counted |> List.collect (fun (name, _) -> tests |> List.filter (fun test -> test.Module = name))
            Some { result with Typed = result.Typed |> Option.map (fun typed -> { typed with Tests = inOrder typed.Tests }) }

    /// `package` as a component build builds it, if its host has a component root: with that root's `run` in place of
    /// its host's (see `Host.AsComponent`).
    let asComponent (package: Package) : Package option =
        package.Host |> Option.bind _.AsComponent |> Option.map (fun host -> { package with Host = Some host })

    /// The compile of the package's `@entry` file, if it has one.
    let entryResult (build: PackageBuild) : CompileResult option =
        build.Package.Entry |> Option.bind (fun entry -> build.Results |> List.tryFind (fst >> (=) entry) |> Option.map snd)

    /// The modules of the package `build` built that mark a def `@component`, by name, in the order of its files.
    let componentModules (build: PackageBuild) : string list =
        build.Results
        |> List.choose (fun (_, result) ->
            match result.ModuleName, result.Typed with
            | Some name, Some typed when typed.ComponentExports |> List.exists (fun export -> export.Module = name) -> Some name
            | _ -> None)

    /// The source of the root a library component is built from: a module that imports each of `modules`, which a
    /// component built from it exports the `@component` defs of, and does nothing else.
    let libraryRootSource (modules: string list) =
        modules |> List.mapi (fun index name -> $"import {name} as __component{index}\n") |> String.concat ""

    /// The compile of the root a library component is built from, for a package `build` built that has no `@entry`
    /// and whose modules mark defs `@component` (see `componentModules`); none for another. The root is no file of the
    /// package's: it is compiled as `__component.nyx` in the package's directory, from `libraryRootSource`.
    let libraryResult (build: PackageBuild) : CompileResult option =
        match build.Package.Entry, componentModules build with
        | None, (_ :: _ as names) ->
            let path = Path.Combine(build.Package.Manifest.Root, "__component.nyx")
            // It is a file of the package, so its imports name the package's modules.
            let packageModules = modules build.Package
            let packageModules = { packageModules with Files = packageModules.Files @ [ path ] }
            Some (Compiler.compileSourceIn packageModules Linters.none path (libraryRootSource names))
        | _ -> None
