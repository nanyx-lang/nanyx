namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text

/// A package's checked file as it is written: the hash of the files it was checked from, the module each file it checked
/// first declares, by key, what checking each of its modules gave, by key, each read only when it is first asked for, and
/// the modules outside it that its checks imported, all the way down, each with the token of the checked file it was
/// read from (see `CheckedFileOrigins`).
type internal CheckedPackageFile =
    { SourceHash: string
      Modules: string list
      Checked: Map<string, Lazy<CheckedResult>>
      Imports: (string * string) list }

/// A package's checked file as a process has read it, with its token and its results recorded as its (see
/// `CheckedFileOrigins`).
type internal CheckedPackage =
    { Token: string
      File: CheckedPackageFile
      Checked: Map<string, Lazy<CheckedResult>> }

/// The checked modules of a package in the package cache, kept beside it so that a build does not check it again (#2167),
/// as the checked library keeps the standard library's (see `CheckedLibrary`). A package from the index or GitHub never
/// changes, so the first build that uses one checks every module of it and writes them to its checked file, in the
/// checked library's format (`BinaryData`), and a later build reads them from there. Only a package in the cache has one:
/// a package found by its path can change with the code around it.
///
/// The file is `.checked/<name>-<version>-<build>.bin` in the cache, for a package from the index, or
/// `<name>-<commit>-<build>.bin` for one from GitHub, where `<build>` is the compiler's build (`CheckedLibrary.build`),
/// so a new compiler, or a new format, writes a file of its own, and deletes the files other builds wrote. It is used only while the package's files hash as they
/// did when it was written, and only with the modules its checks imported as they were then: those read from the same
/// checked files, the checked library's or other packages' (see `CheckedFileOrigins`). Otherwise the package is checked
/// from its source, as before. A file that can't be read is written again.
///
/// A checked module holds the ids of its syntax nodes and symbols, and every `int64` it holds is one. The ids a process
/// gives start at 1, so a module checked by one process would share ids with what another process checks. A package's
/// file holds its ids in a range of its own instead, from `rangeStart` on, chosen by the file's path: when the package is
/// checked, both counters first move past every id given so far, and every id given while it is checked is moved by the
/// same amount into the range as the file is written. An id below that start, which another module this process checked
/// holds, can't be moved, and the file is not written. A process uses one file for each range: in the unlikely case that
/// two files' paths choose the same range, the second is checked from source.
module internal CheckedPackages =
    let private magic = "nanyx checked package"

    /// Where the checked file of the package `name` at `identity` (its version, or the commit it is fetched at) is, in
    /// the package cache `cache`.
    let fileFor (cache: string) (name: string) (identity: string) =
        Path.Combine(cache, ".checked", $"{name}-{identity}-{CheckedLibrary.build}.bin")

    /// Deletes the checked files other builds of the compiler wrote of the package whose checked file is `path`: a
    /// compiler reads only its own.
    let private deleteOthers (path: string) =
        let name = Path.GetFileName path
        let prefix = name.Substring(0, name.Length - CheckedLibrary.build.Length - ".bin".Length)
        for other in Directory.GetFiles(Path.GetDirectoryName path, prefix + "*.bin") do
            let otherName = Path.GetFileName other
            let build = otherName.Substring(prefix.Length, otherName.Length - prefix.Length - ".bin".Length)
            if otherName <> name && fst (Guid.TryParse build) then
                try File.Delete other with :? IOException | :? UnauthorizedAccessException -> ()

    /// How many ids a package's range holds.
    let private rangeSize = 1L <<< 36

    /// The first id of the range the checked file `file` holds its ids in: above the checked library's, at one of 2^24
    /// places chosen by its full path.
    let rangeStart (file: string) =
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes file)
        let place = (int64 hash[0] <<< 16) ||| (int64 hash[1] <<< 8) ||| int64 hash[2]
        (1L <<< 62) + place * rangeSize

    /// The hash of the source files of `modules` as they read now: what its checked file was checked from.
    let sourceHash (modules: PackageModules) =
        let files = modules.Files @ (modules.Referenced |> List.map snd) |> List.distinct |> List.sort
        use sha = IncrementalHash.CreateHash HashAlgorithmName.SHA256
        for file in files do
            sha.AppendData(Encoding.UTF8.GetBytes(file + "\u0000" + File.ReadAllText file + "\u0000"))
        Convert.ToHexString(sha.GetHashAndReset())

    /// The range each checked file this process has read holds its ids in, by its start, with the file.
    let private ranges = ConcurrentDictionary<int64, string>()

    /// An id that doesn't belong in a package's checked file: one this process gave before the package was checked.
    exception private ForeignId of int64

    let private payloadHash (bytes: byte array) (start: int) = Convert.ToHexString(SHA256.HashData(ReadOnlySpan(bytes, start, bytes.Length - start)))

    /// The checked file at `path`, whose bytes are `bytes`, read, if the build of the compiler that is running wrote it,
    /// its bytes are as they were written, and it holds `modules` as their files read now; its modules are read from the
    /// bytes when they are first asked for.
    let private ofBytes (path: string) (modules: PackageModules) (bytes: byte array) : CheckedPackage option =
        use reader = new BinaryReader(new MemoryStream(bytes, false))
        if reader.ReadString() <> magic || reader.ReadString() <> CheckedLibrary.build then
            None
        else
            let token = reader.ReadString()
            let hash = reader.ReadString()
            let start = int reader.BaseStream.Position
            if payloadHash bytes start <> hash then
                None
            else
                let file = BinaryData.read bytes start id typeof<CheckedPackageFile> :?> CheckedPackageFile
                if file.SourceHash <> sourceHash modules || ranges.GetOrAdd(rangeStart path, path) <> path then
                    None
                else
                    Some { Token = token; File = file; Checked = file.Checked |> Map.map (fun _ result -> CheckedFileOrigins.record token result) }

    /// The checked file at `path`, if it is there and holds `modules` as they are (see `ofBytes`); none if it is missing
    /// or can't be read, whatever is wrong with it.
    let tryRead (path: string) (modules: PackageModules) : CheckedPackage option =
        try
            if File.Exists path then ofBytes path modules (File.ReadAllBytes path) else None
        with _ ->
            None

    /// Moves both id counters past every id given so far, and gives the first id they give next: where the ids a package
    /// is checked with start.
    let startIds () : int64 =
        let first = max (Parser.Program.NodeId.next ()) (SymbolId.next ()) + 1L
        Parser.Program.NodeId.startAt first
        SymbolId.startAt first
        first

    /// The last id either counter has given.
    let lastId () : int64 = max (Parser.Program.NodeId.next ()) (SymbolId.next ())

    /// Writes the checked file of the package `modules` to `path`: `checkedModules`, what checking each module of it gave
    /// by key, with the ids from `first` to `last` given while it was checked, and `imports`, the modules outside it its
    /// checks imported, each with the token of the file it was read from. Gives the file as a process reads it, or none
    /// if it can't be written: it holds an id given before `first`, or more ids than a range holds. It is written to a
    /// file of its own first and then renamed, so that another build never reads it half written.
    let write
        (path: string)
        (modules: PackageModules)
        (first: int64, last: int64)
        (checkedModules: (string * CheckedResult) list)
        (imports: (string * string) list)
        : CheckedPackage option =
        let start = rangeStart path
        let moved (id: int64) =
            if id >= first && id <= last then start + (id - first)
            elif id <= 0L || id >= CheckedLibrary.firstId then id
            else raise (ForeignId id)
        let file =
            { SourceHash = sourceHash modules
              Modules = checkedModules |> List.map fst
              Checked = checkedModules |> List.map (fun (key, result) -> key, lazy result) |> Map.ofList
              Imports = imports }
        if last - first >= rangeSize then
            None
        else
            try
                use payload = new MemoryStream()
                (
                    use writer = new BinaryWriter(payload, Encoding.UTF8, true)
                    BinaryData.writeWith writer id moved typeof<CheckedPackageFile> file
                )
                use stream = new MemoryStream()
                (
                    use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                    writer.Write magic
                    writer.Write CheckedLibrary.build
                    writer.Write(Guid.NewGuid().ToString "N")
                    writer.Write(payloadHash (payload.ToArray()) 0)
                    writer.Write(payload.ToArray())
                )
                let bytes = stream.ToArray()
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                let partial = $"{path}.partial-{Guid.NewGuid():N}"
                File.WriteAllBytes(partial, bytes)
                File.Move(partial, path, true)
                deleteOthers path
                ofBytes path modules bytes
            with
            | ForeignId _
            | :? IOException
            | :? UnauthorizedAccessException -> None
