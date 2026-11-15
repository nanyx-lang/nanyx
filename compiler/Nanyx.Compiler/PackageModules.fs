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
      /// The host the package's `@entry` runs under: its manifest's `host`, or `--host`.
      Host: Host
      /// The modules a package that depends on this one may import: its manifest's `publicModules`.
      PublicModules: string list
      /// The packages it depends on directly, each with its own dependencies.
      Dependencies: PackageModules list }

module PackageModules =
    /// Whether two full paths name the same file: file names differ only in case on Windows and macOS, and an editor
    /// may write a drive letter in lower case.
    let internal samePath (a: string) (b: string) =
        String.Equals(a, b, if OperatingSystem.IsLinux() then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase)

    /// The module name each file read so far declares, by full path, with the time it was written and its length
    /// when it was read, so the language server, which builds the table on every change, reads only files that changed.
    let private declaredNames = ConcurrentDictionary<string, DateTime * int64 * string option>()

    /// The name the file at `filePath`, whose text is `source`, declares, with the span of its declaration, and the
    /// file as parsed; None if it does not parse, which compiling it reports.
    let private parse (filePath: string) (source: string) =
        match parseModuleFileWithNames filePath source with
        | Result.Ok (syntax, _, moduleBlocks) -> Some (FileImports.declaration moduleBlocks syntax, syntax)
        | Result.Error _ -> None

    /// The module name the file at `filePath` declares as it is on disk.
    let private declaredName (filePath: string) =
        let info = FileInfo filePath
        match declaredNames.TryGetValue filePath with
        | true, (written, length, name) when written = info.LastWriteTimeUtc && length = info.Length -> name
        | _ ->
            let name = parse filePath (File.ReadAllText filePath) |> Option.bind fst |> Option.map fst
            declaredNames.[filePath] <- (info.LastWriteTimeUtc, info.Length, name)
            name

    /// The modules of the package called `name`, whose manifest is in `root`, whose source files are `files` and whose
    /// `@entry` runs under `host`, without public modules or dependencies.
    let ofFiles (name: string) (root: string) (host: Host) (files: string list) : PackageModules =
        { Package = name
          Host = host
          Root = root
          Files = files
          PublicModules = []
          Dependencies = []
          Modules =
            files
            |> List.choose (fun file -> declaredName file |> Option.map (fun moduleName -> moduleName, file))
            |> List.groupBy fst
            |> List.map (fun (moduleName, declarations) -> moduleName, declarations |> List.map snd)
            |> Map.ofList }

    /// Whether `filePath` is one of the package's source files.
    let contains (modules: PackageModules) (filePath: string) =
        modules.Files |> List.exists (samePath filePath)

    /// The package of the build `modules` that `filePath` is a source file of: the package itself, or one it depends
    /// on, directly or through others.
    let rec ownerOf (modules: PackageModules) (filePath: string) : PackageModules option =
        if contains modules filePath then Some modules else modules.Dependencies |> List.tryPick (fun dependency -> ownerOf dependency filePath)

    /// The package named `name` among those of the build `modules`, the package itself or one it depends on.
    let rec private tryFindPackage (modules: PackageModules) (name: string) : PackageModules option =
        if modules.Package = name then Some modules else modules.Dependencies |> List.tryPick (fun dependency -> tryFindPackage dependency name)

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    /// A file of the package as messages name it: relative to the manifest, with forward slashes.
    let display (modules: PackageModules) (filePath: string) =
        Path.GetRelativePath(modules.Root, filePath).Replace('\\', '/')

    /// The file of the public module `moduleName` of `dependency`, a direct dependency of the package whose file has the
    /// import `item` of `name`.
    let private resolvePublicModule (dependency: PackageModules) (item: ImportItem) (name: string) (moduleName: string) : Result<string, Diagnostic> =
        let isPublic = List.contains moduleName dependency.PublicModules
        match dependency.Modules |> Map.tryFind moduleName with
        | Some (file :: _) when isPublic -> Ok file
        | Some _ ->
            let publicOnes =
                match dependency.PublicModules with
                | [] -> "it has none: a package lists them in its manifest's package publicModules"
                | modules -> $"its public modules are {quoted modules}"
            Error (Diagnostics.at DiagnosticCodes.ModuleNotPublic item.Span $"The module '{moduleName}' of the package '{dependency.Package}' is not public, so another package can't import it: {publicOnes}")
        | None ->
            let hint = NameSuggestions.question (NameSuggestions.closest dependency.PublicModules moduleName)
            Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {name}: the package '{dependency.Package}' has no module '{moduleName}'{hint}")

    /// The file that the import `item` of the module `name`, in a source file of the package `modules`, names: one of the
    /// package's own modules, or a public module of a package it depends on directly. `build` is the package the build
    /// is of, which holds every package the build reaches.
    let private resolveModule (build: PackageModules) (modules: PackageModules) (item: ImportItem) (name: string) : Result<string, Diagnostic> =
        let notFound message = Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {name}: {message}")
        let package, moduleName =
            match name.IndexOf '/' with
            | -1 -> None, name
            | slash -> Some (name.Substring(0, slash)), name.Substring(slash + 1)
        match package with
        | Some other when other <> modules.Package ->
            match modules.Dependencies |> List.tryFind (fun dependency -> dependency.Package = other), tryFindPackage build other with
            | Some dependency, _ -> resolvePublicModule dependency item name moduleName
            | None, Some _ ->
                Error (Diagnostics.at DiagnosticCodes.IndirectDependency item.Span $"The package '{other}' is not a dependency of '{modules.Package}', only of a package it depends on: a package can import only the packages it depends on itself, so add '{other}' to its dependencies")
            | None, None ->
                let dependencies =
                    match modules.Dependencies with
                    | [] -> "it has no dependencies"
                    | dependencies -> $"its dependencies are {quoted (dependencies |> List.map _.Package)}"
                notFound $"there is no package '{other}': '{modules.Package}' can import the standard library, its own modules, and the public modules of the packages it depends on, and {dependencies}"
        | _ ->
            // A module in several files is checked from its first, which brings in the others.
            match modules.Modules |> Map.tryFind moduleName with
            | Some (file :: _) -> Ok file
            | _ ->
                // A file named for the module that declares another name, or none, is most likely the one meant.
                let lastSegment = moduleName.Substring(moduleName.LastIndexOf '.' + 1)
                let namedFor =
                    modules.Files
                    |> List.filter (fun file -> Path.GetFileNameWithoutExtension file = lastSegment)
                    |> List.map (fun file ->
                        match declaredName file with
                        | Some declared -> $"'{display modules file}' declares the module name '{declared}', not '{moduleName}'"
                        | None -> $"'{display modules file}' declares no module name")
                let hint =
                    match namedFor with
                    | [] -> NameSuggestions.question (NameSuggestions.closest modules.Modules.Keys moduleName)
                    | found -> "; " + String.concat "; " found
                notFound $"the package '{modules.Package}' has no module '{moduleName}'{hint}"

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
        match modules, item.Source with
        | Some modules, ModuleImport name ->
            match ownerOf modules importer with
            | Some owner -> resolveModule modules owner item name
            | None -> FileImports.resolvePath (Path.GetDirectoryName importer) item
        | _ -> FileImports.resolvePath (Path.GetDirectoryName importer) item

    /// The errors in the source file at `filePath` of the package `modules`, whose text is `source`: a package's files
    /// must be named modules, without top-level statements. (That files declaring one module are each marked
    /// `@partial` is checked as the module is, by `PartialModules.assemble`.) `compiled` is the module name the file
    /// declares and its syntax, from the compile of it, if that got as far as a tree; if not, the file is parsed here.
    let fileErrors (modules: PackageModules) (filePath: string) (source: string) (compiled: (string option * Module) option) : Diagnostic list =
        let parsed =
            compiled
            |> Option.orElseWith (fun () -> parse filePath source |> Option.map (fun (declaration, syntax) -> Option.map fst declaration, syntax))
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
                    [ Diagnostics.at DiagnosticCodes.ScriptInPackage span $"'{shown}' is in the package '{modules.Package}', so it must declare a module name: start it with module {example}" ]
            let statements =
                syntax
                |> List.tryFind (function Kind (Expr _) -> true | _ -> false)
                |> Option.map (fun statement ->
                    Diagnostics.at DiagnosticCodes.ScriptInPackage statement.Span $"'{shown}' is in the package '{modules.Package}', so it cannot have top-level statements: put them in the package's @entry def")
                |> Option.toList
            named @ statements
