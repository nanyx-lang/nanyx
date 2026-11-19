namespace NyxCompiler

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// What checking a module gave: the module, or the phase it stopped at and why.
type internal CheckedResult = Result<CheckedModule, CompilationPhase * Diagnostic list>

/// A standard-library module as an import of it checks it, with the modules it imports, all the way down, each by its
/// `originKey` and read only when it is first asked for: what `Compiler.typecheckImport` keeps of it for the rest of the
/// process.
type internal CheckedLibraryModule = CheckedResult * (string * Lazy<CheckedResult>) list

/// One module in the checked library: its `originKey`, the hash of the source it was checked from, and the keys of the
/// modules it imports, all the way down.
type internal CheckedLibraryEntry =
    { Key: string
      SourceHash: string
      Imports: string list }

/// A module of a package the compiler ships, as an import of it checks it: what `checkedSourceFiles` in `Compiler`
/// keeps of a source file's module.
type internal CheckedShippedModule =
    { /// Its `originKey`: its file's full path.
      Key: string
      /// Every source file its check read, with the text it read: its own and those of the modules it imports.
      Read: (string * string) list
      Module: CheckedModule
      /// The modules it imports, all the way down, each read only when it is first asked for.
      Imports: (string * Lazy<CheckedResult>) list }

/// A module of a shipped package as the file has it: its key, the files its check read, and the keys of the modules it
/// imports, all the way down.
type internal CheckedShippedEntry =
    { Key: string
      Read: (string * string) list
      Imports: string list }

/// A source file of a package the compiler ships, as the file has it: its full path, its text, and the module it
/// declares, if it declares one, with whether it exports it. It is what `PackageModules` reads of the file to know the
/// package's modules, which a compile of a loose script needs before it imports anything.
type internal CheckedShippedFile =
    { Path: string
      Text: string
      Declares: (string * bool) option }

/// The checked library as it is written: its modules and the modules of the packages the compiler ships, each with the
/// keys of what it imports, and what checking each of them and of their imports gave, by key, each read only when it is
/// first asked for; with the standard-library modules that export each name, and the module each source file of the
/// shipped packages declares, which every compile needs and would otherwise parse every library file for.
type internal CheckedLibraryFile =
    { Modules: CheckedLibraryEntry list
      Shipped: CheckedShippedEntry list
      Checked: Map<string, Lazy<CheckedResult>>
      /// The standard-library modules that export each name, as `ImportFixes.standardLibraryExports` has them.
      Exports: Map<string, string list>
      Files: CheckedShippedFile list }

/// The standard library checked once, when the compiler is built, so that a compile does not check the prelude and the
/// modules it imports again (#837), with the modules of the packages the compiler ships, which a loose script imports
/// through the console host (#1637). The build of `Nanyx.Compiler.CheckedLibrary` checks every module of the library
/// and of the shipped packages and writes them to `checked-library.bin`, which every project that uses the compiler has
/// beside it, and the published `nanyx` too (see `compiler/CheckedLibrary.targets`). A process reads it the first time
/// it imports a library module.
///
/// The file is used only where it is what checking the module would give: it is read only by the build of the compiler
/// that wrote it (`build`, which the library's files are embedded in, so they read as they did then), a library module
/// only for the source it was checked from, and a shipped package's module only while every file its check read reads
/// the same. Any other source, as the editor's unsaved prelude, is checked as before.
///
/// Each module is written once, in a part of its own (see `BinaryData`): a process reads the list of modules, and then
/// only the modules it imports, the first time it does (#1852). A module that refers to another module's types holds a
/// copy of them, since no part refers into another.
///
/// A shipped package's files are where the compiler is (`ShippedPackages.directory`), which is not where the writer
/// was, so the file names a path in that directory relative to it, and a path read back is in this compiler's.
///
/// Its syntax nodes and symbols have ids from `firstId` on, which no process gets to, so they never meet the ids of the
/// modules a process checks itself.
module internal CheckedLibrary =
    /// The first id of a syntax node or a symbol of the checked library.
    let firstId = 1L <<< 56

    let fileName = "checked-library.bin"

    let private magic = "nanyx checked library"

    /// Whether the process reads the checked library: not the one that writes it, which must check every module itself.
    let mutable reading = true

    let private hash (text: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text))

    /// This build of the compiler: the id of its assembly, which changes when any of its code or the library files it
    /// embeds does.
    let build = typeof<CheckedLibraryFile>.Assembly.ManifestModule.ModuleVersionId.ToString()

    /// What a path in the shipped packages' directory starts with in the file, in place of the directory.
    let private shippedMark = "\u0000packages/"

    let private shippedPrefix = ShippedPackages.directory + string Path.DirectorySeparatorChar

    /// A string as the file has it: a path in the shipped packages' directory relative to it, with forward slashes.
    let private relative (text: string) =
        if text.StartsWith(shippedPrefix, StringComparison.Ordinal) then
            shippedMark + text.Substring(shippedPrefix.Length).Replace(Path.DirectorySeparatorChar, '/')
        else
            text

    /// A string the file has, as this compiler has it: a path relative to the shipped packages' directory in it.
    let private absolute (text: string) =
        if text.StartsWith(shippedMark, StringComparison.Ordinal) then
            shippedPrefix + text.Substring(shippedMark.Length).Replace('/', Path.DirectorySeparatorChar)
        else
            text

    /// Writes the library's `modules`, the shipped packages' `shipped`, the library's `exports` and the shipped packages'
    /// source `files` to the file at `path`.
    let write
        (path: string)
        (modules: (string * string * CheckedLibraryModule) list)
        (shipped: CheckedShippedModule list)
        (exports: Map<string, string list>)
        (files: CheckedShippedFile list)
        =
        let checkedResults =
            [ for _, _, (_, imports) in modules do
                  yield! imports
              for key, _, (own, _) in modules do
                  yield key, lazy own
              for entry in shipped do
                  yield! entry.Imports
                  yield entry.Key, lazy (Ok entry.Module) ]
            |> List.fold (fun checkedResults (key, result) -> if Map.containsKey key checkedResults then checkedResults else checkedResults |> Map.add key result) Map.empty
        let file =
            { Modules =
                modules
                |> List.sortBy (fun (key, _, _) -> key)
                |> List.map (fun (key, source, (_, imports)) -> { CheckedLibraryEntry.Key = key; SourceHash = hash source; Imports = List.map fst imports })
              Shipped =
                shipped
                |> List.sortBy _.Key
                |> List.map (fun entry -> { CheckedShippedEntry.Key = entry.Key; Read = entry.Read; Imports = List.map fst entry.Imports })
              Checked = checkedResults
              Exports = exports
              Files = files |> List.sortBy _.Path }
        use stream = File.Create path
        use writer = new BinaryWriter(stream)
        writer.Write magic
        writer.Write build
        BinaryData.write writer relative typeof<CheckedLibraryFile> file

    /// Where the compiler finds the checked library: beside it.
    let path = Path.Combine(AppContext.BaseDirectory, fileName)

    /// The checked library at `path`, if it is there and the build of the compiler `writtenBy` wrote it. Its modules are
    /// read from the file's bytes when they are first asked for.
    let read (path: string) (writtenBy: string) : CheckedLibraryFile option =
        if not (File.Exists path) then
            None
        else
            let bytes = File.ReadAllBytes path
            use reader = new BinaryReader(new MemoryStream(bytes, false))
            if reader.ReadString() <> magic || reader.ReadString() <> writtenBy then
                None
            else
                Some (BinaryData.read bytes (int reader.BaseStream.Position) absolute typeof<CheckedLibraryFile> :?> CheckedLibraryFile)

    let private file = lazy (if reading then read path build else None)

    let private modules =
        lazy (file.Value |> Option.map (fun file -> file.Modules |> List.map (fun entry -> entry.Key, entry) |> Map.ofList) |> Option.defaultValue Map.empty)

    let private shipped =
        lazy (file.Value |> Option.map (fun file -> file.Shipped |> List.map (fun entry -> entry.Key, entry) |> Map.ofList) |> Option.defaultValue Map.empty)

    /// The standard-library modules that export each name, by their import paths (see
    /// `ImportFixes.standardLibraryExportPaths`): as the file has them, or from the library's sources if there is no file.
    let standardLibraryExportPaths : Lazy<Map<string, string list>> =
        lazy
            (match file.Value with
             | Some file -> ImportFixes.exportPaths file.Exports
             | None -> ImportFixes.standardLibraryExportPaths.Value)

    /// Tells `PackageModules` the module each source file of the shipped packages declares, as the file has it, so that
    /// a compile does not parse the files to learn their modules' names (see `PackageModules.knowDeclaration`).
    let knowShippedDeclarations : Lazy<unit> =
        lazy
            (match file.Value with
             | Some file ->
                 for shippedFile in file.Files do
                     PackageModules.knowDeclaration shippedFile.Path shippedFile.Text shippedFile.Declares
             | None -> ())

    /// What checking the module `key` gave, as the file has it, read when it is first asked for.
    let private checkedResult (key: string) = file.Value.Value.Checked[key]

    /// The library module `key` (its `originKey`) as the checked library has it, if the library was checked from
    /// `source`.
    let tryFind (key: string) (source: string) : CheckedLibraryModule option =
        if reading then
            modules.Value
            |> Map.tryFind key
            |> Option.filter (fun entry -> entry.SourceHash = hash source)
            |> Option.map (fun entry -> (checkedResult key).Value, entry.Imports |> List.map (fun import -> import, checkedResult import))
        else
            None

    /// The module of the shipped package's file `key` (its `originKey`) as the checked library has it, if every file its
    /// check read reads the same through `readFile`.
    let tryFindShipped (key: string) (readFile: string -> string) : CheckedShippedModule option =
        let readsTheSame (file: string, text: string) =
            try readFile file = text with _ -> false
        if reading then
            shipped.Value
            |> Map.tryFind key
            |> Option.filter (fun entry -> List.forall readsTheSame entry.Read)
            |> Option.bind (fun entry ->
                match (checkedResult key).Value with
                | Ok checkedModule ->
                    Some { CheckedShippedModule.Key = key; Read = entry.Read; Module = checkedModule; Imports = entry.Imports |> List.map (fun import -> import, checkedResult import) }
                | Error _ -> None)
        else
            None
