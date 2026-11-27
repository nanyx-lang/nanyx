namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open Parser.Program

/// A package's modules by the names its source files declare, which the imports in its files are resolved through:
/// `import greetings` and `import acme.web/greetings` both reach the file that declares `module greetings`, wherever
/// it is in the package. It holds the packages it depends on too, each with its own modules, so that
/// `import acme.util/strings` reaches a public module of a direct dependency.
type PackageModules =
    { /// The package's name, which an import of one of its own modules may start with.
      Package: string
      /// The directory of the package's manifest, which messages name the package's files relative to.
      Root: string
      /// The full paths of the package's source files, in order.
      Files: string list
      /// Each module name the package's files declare, with the files that declare it, in order: several for a module
      /// split across files, each marked `@partial` (see `PartialModules`).
      Modules: Map<string, string list>
      /// The host the package's `@entry` runs under: its manifest's `host`, or `--host`. None for a library, which can
      /// have no `@entry`.
      Host: Host option
      /// The modules of the host's package, which the host's import, and the host's own imports, resolve in; none for a
      /// library.
      HostPackage: PackageModules option
      /// The modules a package that depends on this one may import: those its files declare with `export module`.
      PublicModules: string list
      /// The packages it depends on directly, each with its own dependencies.
      Dependencies: PackageModules list
      /// The files from GitHub it depends on, each a module of the package, by the module name its manifest gives it,
      /// with the full path of the file it is fetched to. They are among `Modules`, but not `Files`: they are not the
      /// package's own source files, and they may import only the standard library.
      Referenced: (string * string) list
      /// Whether this is a script's build, whose `::host` and `::install` directives say what a manifest would (see
      /// `Script`): it has no files of its own, and every file of the build that is in no package it depends on is the
      /// program's, its imports found by their paths.
      Script: bool
      /// For a package in the package cache, which never changes, the file its checked modules are kept in (see
      /// `CheckedPackages`); none for any other, whose files can change.
      CheckedFile: string option
      /// Whether the package is a host library, whose modules may require `HostRuntime` when the build host's package
      /// depends on it (see `HostLibraries.field`).
      HostLibrary: bool
      /// Whether the package's manifest declares it a host (see `HostPackages`), whose modules may require `HostRuntime`.
      DeclaresHost: bool
      /// For a test build's modules, the full path of the build's root, which runs one test of the program's: each of
      /// the program's other files gives it a way to run its own (see `Attributes.testDispatcher`), and its `@entry` is
      /// not one. None for any other build.
      TestRoot: string option
      /// In a test build, the test host, which the build's root runs under, with its package's modules; the program's
      /// own modules keep `Host`, whose prelude they see. None for any other build.
      TestHost: (Host * PackageModules) option }

module PackageModules =
    /// Whether two full paths name the same file: file names differ only in case on Windows and macOS, and an editor
    /// may write a drive letter in lower case.
    let internal samePath (a: string) (b: string) =
        String.Equals(a, b, if OperatingSystem.IsLinux() then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase)

    /// The module name each file read so far declares, and whether it exports it, by full path, with the time it was
    /// written and its length when it was read, so the language server, which builds the table on every change, reads
    /// only files that changed. None for a file that does not parse.
    let private declaredNames = ConcurrentDictionary<string, DateTime * int64 * (string * bool) option option>()

    /// The name the file at `filePath`, whose text is `source`, declares, with the span of its declaration, whether
    /// it exports it, and the file as parsed; None if it does not parse, which compiling it reports.
    let private parse (filePath: string) (source: string) =
        match parseModuleFileWithNames filePath source with
        | Result.Ok (syntax, _, moduleBlocks) -> Some (FileImports.declaration moduleBlocks syntax, FileImports.exportsModule moduleBlocks syntax, syntax)
        | Result.Error _ -> None

    /// The module name the file at `filePath` declares as it is on disk, and whether it exports it; None if it declares
    /// none, and None in the outer option if it does not parse.
    let private readDeclaration (filePath: string) =
        let info = FileInfo filePath
        match declaredNames.TryGetValue filePath with
        | true, (written, length, declared) when written = info.LastWriteTimeUtc && length = info.Length -> declared
        | _ ->
            let declared =
                parse filePath (File.ReadAllText filePath)
                |> Option.map (fun (declaration, exported, _) -> declaration |> Option.map (fun (name, _) -> name, exported))
            declaredNames.[filePath] <- (info.LastWriteTimeUtc, info.Length, declared)
            declared

    /// Takes `declared` as the module name the file at `filePath` declares, and whether it exports it, if the file reads
    /// `text`, as another reading of it found (the checked library's: see `CheckedLibrary.knowShippedDeclarations`), so
    /// that it is not parsed again. A file that reads otherwise, or that has been read already, is left as it is.
    let internal knowDeclaration (filePath: string) (text: string) (declared: (string * bool) option) =
        let info = FileInfo filePath
        let readsTheSame () =
            try File.ReadAllText filePath = text with _ -> false
        if info.Exists && not (declaredNames.ContainsKey filePath) && readsTheSame () then
            declaredNames.TryAdd(filePath, (info.LastWriteTimeUtc, info.Length, Some declared)) |> ignore

    /// The module name the file at `filePath` declares as it is on disk, and whether it exports it, if it declares one;
    /// None in the outer option if it does not parse.
    let internal declaration (filePath: string) = readDeclaration filePath

    /// The module name the file at `filePath` declares as it is on disk, and whether it exports it.
    let internal declaredModule (filePath: string) = readDeclaration filePath |> Option.flatten

    /// The module name the file at `filePath` declares as it is on disk.
    let private declaredName (filePath: string) = declaredModule filePath |> Option.map fst

    /// Whether the file at `filePath`, as it is on disk, parses. One not on disk yet (an editor's new buffer) has
    /// nothing to fail.
    let private parses (filePath: string) = not (File.Exists filePath) || (readDeclaration filePath).IsSome

    /// `modules` with the modules `declared` gives its files, each module name and whether it is exported with the file
    /// that declares it, and the modules of the files from GitHub it depends on. A module split across files is public
    /// when one of them exports it.
    let private declaring (declared: ((string * bool) * string) list) (modules: PackageModules) =
        let own =
            declared
            |> List.groupBy (fst >> fst)
            |> List.map (fun (moduleName, declarations) -> moduleName, declarations |> List.map snd)
            |> Map.ofList
        { modules with
            PublicModules = declared |> List.filter (fun ((_, exported), _) -> exported) |> List.map (fst >> fst) |> List.distinct
            Modules = modules.Referenced |> List.fold (fun found (moduleName, file) -> Map.add moduleName [ file ] found) own }

    /// The modules of the package called `name`, whose manifest is in `root`, whose source files are `files` and whose
    /// `@entry` runs under `host`, with the modules it exports, without dependencies. A module split across files is
    /// public when one of them exports it.
    let ofFiles (name: string) (root: string) (host: Host option) (files: string list) : PackageModules =
        { Package = name
          Host = host
          HostPackage = None
          Root = root
          Files = files
          PublicModules = []
          Dependencies = []
          Referenced = []
          Script = false
          CheckedFile = None
          HostLibrary = false
          DeclaresHost = false
          TestRoot = None
          TestHost = None
          Modules = Map.empty }
        |> declaring (files |> List.choose (fun file -> declaredModule file |> Option.map (fun moduleName -> moduleName, file)))

    /// `modules` with the files from GitHub `referenced` names, each a module name and the full path of its file.
    let withReferenced (referenced: (string * string) list) (modules: PackageModules) : PackageModules =
        { modules with
            Referenced = referenced
            Modules = referenced |> List.fold (fun found (moduleName, file) -> Map.add moduleName [ file ] found) modules.Modules }

    /// `modules` with the names its own files declare read from the texts `readFile` gives them rather than from disk:
    /// an editor's unsaved text, or a rename's.
    let reading (readFile: string -> string) (modules: PackageModules) : PackageModules =
        modules
        |> declaring (
            modules.Files
            |> List.choose (fun file ->
                parse file (readFile file)
                |> Option.bind (fun (declaration, exported, _) -> declaration |> Option.map (fun (moduleName, _) -> (moduleName, exported), file))))

    /// Whether `filePath` is one of the package's source files.
    let contains (modules: PackageModules) (filePath: string) =
        modules.Files |> List.exists (samePath filePath)

    /// The package of the build `modules` that `filePath` is a source file of: the package itself, or one it depends
    /// on, directly or through others.
    let rec ownerOf (modules: PackageModules) (filePath: string) : PackageModules option =
        if contains modules filePath then Some modules else modules.Dependencies |> List.tryPick (fun dependency -> ownerOf dependency filePath)

    /// The module name of the file from GitHub at `filePath`, if it is one that a package of the build `modules`
    /// depends on.
    let rec referencedName (modules: PackageModules) (filePath: string) : string option =
        modules.Referenced
        |> List.tryPick (fun (moduleName, file) -> if samePath file filePath then Some moduleName else None)
        |> Option.orElseWith (fun () -> modules.Dependencies |> List.tryPick (fun dependency -> referencedName dependency filePath))

    /// Whether `filePath` is a file of the program the build `modules` is of, rather than of a library it depends on: a
    /// source file of the package, or, in a script's build, any file in no package it depends on. A file from GitHub
    /// is neither: it is a library's, though it is a module of the package.
    let isProgramFile (modules: PackageModules) (filePath: string) =
        if modules.Script then (ownerOf modules filePath).IsNone && (referencedName modules filePath).IsNone else contains modules filePath

    /// The package named `name` among those of the build `modules`, the package itself or one it depends on.
    let rec private tryFindPackage (modules: PackageModules) (name: string) : PackageModules option =
        if modules.Package = name then Some modules else modules.Dependencies |> List.tryPick (fun dependency -> tryFindPackage dependency name)

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    /// `path` relative to `root`, with forward slashes, as a manifest and messages write it.
    let relativePath (root: string) (path: string) = Path.GetRelativePath(root, path).Replace('\\', '/')

    /// The name a program shows for the source file at `file`, a path as a span names it: its path from `root`, the root
    /// of its package, with `/` between its parts; its name alone without a root (a script) or when it is outside the
    /// root. A crash's place, a `dbg`'s, and an error deferred to run time name a file so.
    let shownFile (root: string option) (file: string) =
        match root with
        | _ when file = "" -> ""
        | Some root ->
            let relative = Path.GetRelativePath(root, file)
            if relative.StartsWith ".." || Path.IsPathRooted relative then Path.GetFileName file
            else relative.Replace('\\', '/')
        | None -> Path.GetFileName file

    /// Where `span` is, as a program that names its files from `root` writes a place in a message: `file:line:column: `,
    /// the file as `shownFile` names it, or the line and column alone for a span with no file.
    let placeOf (root: string option) (span: SourceSpan) =
        if span.File = "" then $"{span.StartLine + 1}:{span.StartColumn + 1}: "
        else $"{shownFile root span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: "

    /// A file of the package as messages name it: relative to the manifest, with forward slashes.
    let display (modules: PackageModules) (filePath: string) = relativePath modules.Root filePath

    /// The file of the public module `moduleName` of `dependency`, a direct dependency of the package whose file has the
    /// import `item` of `name`.
    let private resolvePublicModule (dependency: PackageModules) (item: ImportItem) (name: string) (moduleName: string) : Result<string, Diagnostic> =
        let isPublic = List.contains moduleName dependency.PublicModules
        match dependency.Modules |> Map.tryFind moduleName with
        | Some (file :: _) when isPublic -> Ok file
        | Some _ ->
            let publicOnes =
                match dependency.PublicModules with
                | [] -> "it has none, and a module is public when its file declares it with `export module`"
                | modules -> $"its public modules are {quoted modules}"
            Error (Diagnostics.at DiagnosticCodes.ModuleNotPublic item.Span $"The module '{moduleName}' of the package '{dependency.Package}' is not public, so another package can't import it: {publicOnes}" |> Diagnostics.about moduleName)
        | None ->
            let hint = NameSuggestions.question (NameSuggestions.closest dependency.PublicModules moduleName)
            Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"The package '{dependency.Package}' has no module '{moduleName}'{hint}" |> Diagnostics.about name)

    /// The file that the import `item` of the module `name`, in a source file of the package `modules`, names: one of the
    /// package's own modules, or a public module of a package it depends on directly. `build` is the package the build
    /// is of, which holds every package the build reaches.
    let private resolveModule (build: PackageModules) (modules: PackageModules) (item: ImportItem) (name: string) : Result<string, Diagnostic> =
        let notFound message = Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span message |> Diagnostics.about name)
        let package, moduleName =
            match name.IndexOf '/' with
            | -1 -> None, name
            | slash -> Some (name.Substring(0, slash)), name.Substring(slash + 1)
        match package with
        | Some other when other <> modules.Package ->
            match modules.Dependencies |> List.tryFind (fun dependency -> dependency.Package = other), tryFindPackage build other with
            | Some dependency, _ -> resolvePublicModule dependency item name moduleName
            | None, Some _ ->
                Error (Diagnostics.at DiagnosticCodes.IndirectDependency item.Span $"The package '{other}' is not a dependency of '{modules.Package}', only of a package it depends on, and a package can import only the packages it depends on itself: add '{other}' to its dependencies" |> Diagnostics.about other)
            | None, None when (RenamedPackages.inImport name).IsSome -> notFound (RenamedPackages.inImport name).Value
            | None, None ->
                let dependencies =
                    match modules.Dependencies with
                    | [] -> "it has no dependencies"
                    | dependencies -> $"its dependencies are {quoted (dependencies |> List.map _.Package)}"
                notFound $"There is no package '{other}' to import from, since '{modules.Package}' can import the standard library, its own modules, and the public modules of the packages it depends on, and {dependencies}"
        | _ ->
            // A module in several files is checked from its first, which brings in the others.
            match modules.Modules |> Map.tryFind moduleName with
            | Some (file :: _) -> Ok file
            | _ ->
                // A file named for the module that declares another name, or none, is most likely the one meant.
                let lastSegment = moduleName.Substring(moduleName.LastIndexOf '.' + 1)
                let namedFor = modules.Files |> List.filter (fun file -> Path.GetFileNameWithoutExtension file = lastSegment)
                match namedFor |> List.tryFind (parses >> not) with
                // One that doesn't parse can't say what it declares: it is imported, and its parse error reported.
                | Some file -> Ok file
                | None ->
                    let hint =
                        match namedFor, modules.Files |> List.filter (parses >> not) with
                        | [], [] -> NameSuggestions.question (NameSuggestions.closest modules.Modules.Keys moduleName)
                        | [], unparsed ->
                            let files = unparsed |> List.map (fun file -> $"'{display modules file}'")
                            let parse = if files.Length = 1 then "doesn't parse" else "don't parse"
                            $", though it may be declared by {Diagnostics.listed files}, which {parse}"
                        | found, _ ->
                            let declarations =
                                found
                                |> List.map (fun file ->
                                    match declaredName file with
                                    | Some declared -> $"'{display modules file}' declares the module name '{declared}', not '{moduleName}'"
                                    | None -> $"'{display modules file}' declares no module name")
                            ", but " + Diagnostics.listed declarations
                    notFound $"The package '{modules.Package}' has no module '{moduleName}'{hint}"

    /// The file of the module `name`, by its name alone or after the package's name, among the modules of `modules`
    /// alone, for the import `item`: how a host's import, and the imports in a host's own files, are resolved (see
    /// `ShippedPackages`).
    let resolveWithin (modules: PackageModules) (item: ImportItem) (name: string) : Result<string, Diagnostic> =
        resolveModule modules modules item name

    /// The full path of the file the import `item`, in the file at `importer`, names. In a source file of the package
    /// `modules`, or of a package it depends on, a module import names one of that package's own modules, by its name
    /// alone or after the package's name, wherever its file is, or a public module of a package it depends on directly,
    /// after that package's name; anywhere else, and for an import by file path, it names a file relative to the
    /// importer (see `FileImports.resolvePath`).
    let resolveImport (modules: PackageModules option) (importer: string) (item: ImportItem) : Result<string, Diagnostic> =
        let shown = match item.Source with ModuleImport name -> name | PathImport path -> path
        match modules, item.Source with
        // A file from GitHub has no manifest to say what it depends on, so it imports only the standard library, which
        // is resolved before this.
        | Some modules, _ when (referencedName modules importer).IsSome ->
            Error(
                Diagnostics.at
                    DiagnosticCodes.InvalidReferencedFile
                    item.Span
                    $"'{(referencedName modules importer).Value}' is a file from GitHub, which has no manifest to say what it depends on, so it can import only the standard library, not '{shown}'"
                |> Diagnostics.about (referencedName modules importer).Value
            )
        | Some modules, ModuleImport name ->
            match ownerOf modules importer with
            | Some owner -> resolveModule modules owner item name
            // A script's file imports a public module of a package its `::install` lines name after the package's name.
            | None when modules.Script && modules.Dependencies |> List.exists (fun dependency -> name.StartsWith(dependency.Package + "/")) ->
                resolveModule modules modules item name
            // And a file from GitHub its `::install` lines name by its module name.
            | None when modules.Script && modules.Referenced |> List.exists (fst >> (=) name) ->
                resolveModule modules modules item name
            | None -> FileImports.resolvePath (Path.GetDirectoryName importer) item
        | _ -> FileImports.resolvePath (Path.GetDirectoryName importer) item

    /// The errors in the source file at `filePath` of the package `modules`, whose text is `source`: a package's files
    /// must be named modules, without top-level statements. (That files declaring one module are each marked
    /// `@partial` is checked as the module is, by `PartialModules.assemble`.) `compiled` is the module name the file
    /// declares and its syntax, from the compile of it, if that got as far as a tree; if not, the file is parsed here.
    let fileErrors (modules: PackageModules) (filePath: string) (source: string) (compiled: (string option * Module) option) : Diagnostic list =
        let parsed =
            compiled
            |> Option.orElseWith (fun () -> parse filePath source |> Option.map (fun (declaration, _, syntax) -> Option.map fst declaration, syntax))
        match parsed with
        | None -> []
        | Some (declaration, syntax) ->
            let shown = display modules filePath
            let named =
                match declaration with
                | Some _ -> []
                | None ->
                    let firstLine = match source.IndexOf '\n' with -1 -> source.Length | newline -> newline
                    let span = { File = filePath; Start = 0; End = firstLine; StartLine = 0; StartColumn = 0 }
                    let example = Path.GetFileNameWithoutExtension filePath
                    [ Diagnostics.at DiagnosticCodes.ScriptInPackage span $"'{shown}' is in the package '{modules.Package}', so it must declare a module name: start it with `module {example}`" |> Diagnostics.about shown ]
            let statements =
                syntax
                |> List.tryFind (function Kind (Expr _) -> true | _ -> false)
                |> Option.map (fun statement ->
                    Diagnostics.at DiagnosticCodes.ScriptInPackage statement.Span $"'{shown}' is in the package '{modules.Package}', so it cannot have top-level statements: put them in the package's `@entry` def" |> Diagnostics.about shown)
                |> Option.toList
            named @ statements
