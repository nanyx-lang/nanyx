namespace NyxCompiler

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// A standard-library module as an import of it checks it, with the modules it imports, all the way down, each by its
/// `originKey`: what `Compiler.typecheckImport` keeps of it for the rest of the process.
type internal CheckedLibraryModule =
    Result<CheckedModule, CompilationPhase * Diagnostic list> * (string * Result<CheckedModule, CompilationPhase * Diagnostic list>) list

/// One module in the checked library: its `originKey`, the hash of the source it was checked from, and what the check
/// gave.
type internal CheckedLibraryEntry =
    { Key: string
      SourceHash: string
      Checked: CheckedLibraryModule }

/// A module of a package the compiler ships, as an import of it checks it: what `checkedSourceFiles` in `Compiler`
/// keeps of a source file's module.
type internal CheckedShippedModule =
    { /// Its `originKey`: its file's full path.
      Key: string
      /// Every source file its check read, with the text it read: its own and those of the modules it imports.
      Read: (string * string) list
      Module: CheckedModule
      /// The modules it imports, all the way down.
      Imports: (string * Result<CheckedModule, CompilationPhase * Diagnostic list>) list }

/// The checked library as it is written: the hash of every file of the standard library it was checked from, its
/// modules, and the modules of the packages the compiler ships.
type internal CheckedLibraryFile =
    { LibraryHash: string
      Modules: CheckedLibraryEntry list
      Shipped: CheckedShippedModule list }

/// The standard library checked once, when the compiler is built, so that a compile does not check the prelude and the
/// modules it imports again (#837), with the modules of the packages the compiler ships, which a loose script imports
/// through the console host (#1637). The build of `Nanyx.Compiler.CheckedLibrary` checks every module of the library
/// and of the shipped packages and writes them to `checked-library.bin`, which every project that uses the compiler has
/// beside it, and the published `nanyx` too (see `compiler/CheckedLibrary.targets`). A process reads it the first time
/// it imports a library module.
///
/// The file is used only where it is what checking the module would give: it is read only by a compiler whose types
/// are written as those of the compiler that wrote it (`BinaryData.schema`), only while every file of the library reads
/// as it did then, a library module only for the source it was checked from, and a shipped package's module only while
/// every file its check read reads the same. Any other source, as a development build's changed library or the
/// editor's unsaved prelude, is checked as before.
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

    /// The hash of every file of the standard library, as this compiler embeds it.
    let libraryHash =
        lazy
            (StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames
             |> List.collect StandardLibrary.moduleSources
             |> List.sortBy fst
             |> List.map (fun (name, source) -> name + "\n" + source)
             |> String.concat "\n\u0000"
             |> hash)

    let private schema = lazy (BinaryData.schema typeof<CheckedLibraryFile>)

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

    /// Writes the library's `modules` and the shipped packages' `shipped` to the file at `path`.
    let write (path: string) (modules: (string * string * CheckedLibraryModule) list) (shipped: CheckedShippedModule list) =
        use stream = File.Create path
        use writer = new BinaryWriter(stream)
        writer.Write magic
        writer.Write schema.Value
        let file =
            { LibraryHash = libraryHash.Value
              Modules =
                modules
                |> List.sortBy (fun (key, _, _) -> key)
                |> List.map (fun (key, source, checkedModule) -> { Key = key; SourceHash = hash source; Checked = checkedModule })
              Shipped = shipped |> List.sortBy _.Key }
        BinaryData.write writer relative typeof<CheckedLibraryFile> file

    /// Where the compiler finds the checked library: beside it.
    let path = Path.Combine(AppContext.BaseDirectory, fileName)

    /// The checked library at `path`, if it is there, this compiler can read it, and it was checked from the library
    /// whose files hash to `library`.
    let read (path: string) (library: string) : CheckedLibraryFile option =
        if not (File.Exists path) then
            None
        else
            use stream = new BufferedStream(File.OpenRead path, 1 <<< 16)
            use reader = new BinaryReader(stream)
            if reader.ReadString() <> magic || reader.ReadString() <> schema.Value then
                None
            else
                match BinaryData.read reader absolute typeof<CheckedLibraryFile> :?> CheckedLibraryFile with
                | file when file.LibraryHash = library -> Some file
                | _ -> None

    let private file = lazy (if reading then read path libraryHash.Value else None)

    let private modules =
        lazy (file.Value |> Option.map (fun file -> file.Modules |> List.map (fun entry -> entry.Key, entry) |> Map.ofList) |> Option.defaultValue Map.empty)

    let private shipped =
        lazy (file.Value |> Option.map (fun file -> file.Shipped |> List.map (fun entry -> entry.Key, entry) |> Map.ofList) |> Option.defaultValue Map.empty)

    /// The library module `key` (its `originKey`) as the checked library has it, if the library was checked from
    /// `source`.
    let tryFind (key: string) (source: string) : CheckedLibraryModule option =
        if reading then
            modules.Value |> Map.tryFind key |> Option.filter (fun entry -> entry.SourceHash = hash source) |> Option.map _.Checked
        else
            None

    /// The module of the shipped package's file `key` (its `originKey`) as the checked library has it, if every file its
    /// check read reads the same through `readFile`.
    let tryFindShipped (key: string) (readFile: string -> string) : CheckedShippedModule option =
        let readsTheSame (file: string, text: string) =
            try readFile file = text with _ -> false
        if reading then shipped.Value |> Map.tryFind key |> Option.filter (fun entry -> List.forall readsTheSame entry.Read) else None
