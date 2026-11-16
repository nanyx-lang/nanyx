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

/// The checked library as it is written: the hash of every file of the standard library it was checked from, and its
/// modules.
type internal CheckedLibraryFile =
    { LibraryHash: string
      Modules: CheckedLibraryEntry list }

/// The standard library checked once, when the compiler is built, so that a compile does not check the prelude and the
/// modules it imports again (#837). The build of `Nanyx.Compiler.CheckedLibrary` checks every module of the library and
/// writes them to `checked-library.bin`, which every project that uses the compiler has beside it, and the published
/// `nanyx` too (see `compiler/CheckedLibrary.targets`). A process reads it the first time it imports a library module.
///
/// The file is used only where it is what checking the module would give: it is read only by a compiler whose types
/// are written as those of the compiler that wrote it (`BinaryData.schema`), only while every file of the library reads
/// as it did then, and a module only for the source it was checked from. Any other source, as a development build's
/// changed library or the editor's unsaved prelude, is checked as before.
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

    /// Writes `modules` to the file at `path`.
    let write (path: string) (modules: (string * string * CheckedLibraryModule) list) =
        use stream = File.Create path
        use writer = new BinaryWriter(stream)
        writer.Write magic
        writer.Write schema.Value
        let file =
            { LibraryHash = libraryHash.Value
              Modules =
                modules
                |> List.sortBy (fun (key, _, _) -> key)
                |> List.map (fun (key, source, checkedModule) -> { Key = key; SourceHash = hash source; Checked = checkedModule }) }
        BinaryData.write writer typeof<CheckedLibraryFile> file

    /// Where the compiler finds the checked library: beside it.
    let path = Path.Combine(AppContext.BaseDirectory, fileName)

    /// The modules of the checked library at `path`, by key, if it is there, this compiler can read it, and it was
    /// checked from the library whose files hash to `library`; else none.
    let read (path: string) (library: string) : Map<string, CheckedLibraryEntry> =
        if not (File.Exists path) then
            Map.empty
        else
            use stream = new BufferedStream(File.OpenRead path, 1 <<< 16)
            use reader = new BinaryReader(stream)
            if reader.ReadString() <> magic || reader.ReadString() <> schema.Value then
                Map.empty
            else
                match BinaryData.read reader typeof<CheckedLibraryFile> :?> CheckedLibraryFile with
                | file when file.LibraryHash = library -> file.Modules |> List.map (fun entry -> entry.Key, entry) |> Map.ofList
                | _ -> Map.empty

    let private modules = lazy (read path libraryHash.Value)

    /// The library module `key` (its `originKey`) as the checked library has it, if the library was checked from
    /// `source`.
    let tryFind (key: string) (source: string) : CheckedLibraryModule option =
        if reading then
            modules.Value |> Map.tryFind key |> Option.filter (fun entry -> entry.SourceHash = hash source) |> Option.map _.Checked
        else
            None
