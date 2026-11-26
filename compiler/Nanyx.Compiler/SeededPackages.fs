namespace NyxCompiler

open System
open System.IO
open System.IO.Compression
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Seeding the packages the compiler ships into a package store (#2701). Each is packed as the compiler's release packs
/// it, which gives the same bytes on any machine (see `PackageArchive`), so the same SHA-256 the package index records
/// for the release's own copy; unpacked into the store's cache, as a download is; and listed, with its SHA-256, in an
/// index of their own in the cache (`PackageStore.shippedIndex`), which `PackageIndex.listing` reads beside the
/// package index. So a build finds them with no network, and locks them as it locks any package from the index.
module SeededPackages =
    let private options = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// The index file of `manifest`'s package, listing its one version, packed to `bytes` at `url`, relative to the
    /// index file: as the package index's sync records a release, with the compilers it builds with and the versions of
    /// the packages it depends on, as its manifest writes them.
    let private indexFile (manifest: Manifest) (url: string) (bytes: byte array) : string =
        let dependencies = JsonObject()
        for dependency in manifest.Dependencies do
            match dependency.Source with
            | FromIndex requirement -> dependencies.[dependency.Name] <- JsonValue.Create requirement.Written
            | DependencySource.Shipped -> dependencies.[dependency.Name] <- JsonValue.Create Toolchain.version
            | DependencySource.AtPath _
            | OnGitHub _ -> ()
        let version = JsonObject()
        version.["version"] <- JsonValue.Create manifest.Version
        version.["url"] <- JsonValue.Create url
        version.["sha256"] <- JsonValue.Create(PackageArchive.sha256 bytes)
        version.["nanyx"] <- manifest.Nanyx |> Option.map (fun nanyx -> JsonValue.Create(string nanyx) :> JsonNode) |> Option.toObj
        version.["dependencies"] <- dependencies
        version.["unlisted"] <- JsonValue.Create false
        version.["available"] <- JsonValue.Create true
        let file = JsonObject()
        file.["name"] <- JsonValue.Create manifest.Name
        file.["versions"] <- JsonArray(version)
        file.ToJsonString options + "\n"

    /// Each package the compiler ships, packed, with its manifest; or why one can't be. They depend only on the packages
    /// the compiler ships, so they pack the same whatever `store` reads them through.
    let private packWith (store: PackageStore) : Result<(Manifest * byte array) list, string list> =
        ShippedPackages.names ()
        |> List.map (fun name ->
            Package.findIn store (ShippedPackages.directoryOf name)
            |> Result.bind (fun package ->
                PackageApi.pack package
                |> Result.map (fun bytes -> package.Manifest, bytes)
                |> Result.mapError (List.map Manifest.describe)))
        |> List.fold
            (fun found packedOne ->
                match found, packedOne with
                | Result.Ok packages, Result.Ok one -> Result.Ok(packages @ [ one ])
                | Result.Ok _, Result.Error errors -> Result.Error errors
                | Result.Error errors, Result.Ok _ -> Result.Error errors
                | Result.Error errors, Result.Error more -> Result.Error(errors @ more))
            (Result.Ok [])

    /// The packages the compiler ships, packed (see `packWith`), once a process: they are the same for every store, so a
    /// process that seeds several, as the tests do, packs them once. One that can't be packed is tried again.
    let private packedOnce = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Result<(Manifest * byte array) list, string list>>>()

    let private packed (store: PackageStore) : Result<(Manifest * byte array) list, string list> =
        let key = ShippedPackages.fingerprint.Value
        let result = packedOnce.GetOrAdd(key, fun _ -> lazy (packWith store)).Value
        if Result.isError result then packedOnce.TryRemove key |> ignore
        result

    /// How many times this process has written each seeded index, by its directory: the work seeding does, packing the
    /// packages, which a cache seeded already saves.
    let private written = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

    /// How many times this process has written the seeded index of `store` (see `written`).
    let internal timesWritten (store: PackageStore) =
        match written.TryGetValue(PackageStore.shippedIndex store) with
        | true, times -> times
        | false, _ -> 0

    /// Writes the index of the packages the compiler ships to `directory`, which must not exist yet, laid out as the
    /// package index is: its `config.json`, an index file for each, and each one's `.nyxpkg` under `archives`. It is
    /// written, and so counted, for the index of `store`.
    let private writeIndex (store: PackageStore) (directory: string) : Result<unit, string list> =
        written.AddOrUpdate(PackageStore.shippedIndex store, 1, fun _ times -> times + 1) |> ignore
        packed store
        |> Result.map (fun packages ->
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(directory).FullName, "config.json"), $$"""{ "format": {{PackageIndex.format}}, "index": "index/" }""" + "\n")
            Directory.CreateDirectory(Path.Combine(directory, "archives")) |> ignore
            for manifest, bytes in packages do
                let archive = PackageArchive.fileName manifest
                File.WriteAllBytes(Path.Combine(directory, "archives", archive), bytes)
                let indexDirectory = Directory.CreateDirectory(Path.Combine(directory, "index", PackageIndex.namespaceOf manifest.Name))
                File.WriteAllText(Path.Combine(indexDirectory.FullName, manifest.Name + ".json"), indexFile manifest $"../../archives/{archive}" bytes))

    /// The entries of the packed package `bytes` that the directory `unpacked` doesn't hold as they are: missing, or
    /// with other bytes.
    let private differing (bytes: byte array) (unpacked: string) : string list =
        use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
        [ for entry in zip.Entries do
              let path = Path.Combine(unpacked, entry.FullName)
              use stream = entry.Open()
              use copy = new MemoryStream()
              stream.CopyTo copy
              if not (File.Exists path) || File.ReadAllBytes path <> copy.ToArray() then
                  entry.FullName ]

    /// Unpacks the packed package `bytes`, `name` at `version`, into the cache of `store`, if the cache doesn't hold it
    /// yet; or, if it holds another package of that name and version, says so: a package in the cache is never changed.
    let private unpacked (store: PackageStore) (name: string) (version: SemanticVersion) (bytes: byte array) : Result<unit, string> =
        let directory = PackageStore.directoryOf store name version
        if not (Directory.Exists directory) then
            PackageArchive.unpack bytes directory
            |> Result.mapError (fun message -> $"The copy of '{name}' {version} the compiler ships can't be unpacked into the package cache: {message}")
        else
            match differing bytes directory with
            | [] -> Result.Ok()
            | files ->
                let named = files |> List.map (fun file -> $"'{file}'") |> Diagnostics.listed
                Result.Error
                    $"The package cache holds a '{name}' {version}, in '{directory}', that isn't the one this compiler ships: its {named} differ. A package in the cache is never changed, so delete that directory, and the next build puts this compiler's copy there"

    /// `seed`, done now: the index written, if it isn't, and each package unpacked into the cache, if it isn't.
    let private seedOnce (store: PackageStore) : Result<string, string list> =
        let target = PackageStore.shippedIndex store
        let written =
            if Directory.Exists target then
                Result.Ok()
            else
                // Made beside it and moved into place, so that a reader sees all of it or none.
                let staging = $"{target}.{Guid.NewGuid():N}"
                writeIndex store staging
                |> Result.map (fun () ->
                    try
                        Directory.Move(staging, target)
                    with :? IOException when Directory.Exists target ->
                        Directory.Delete(staging, true))
                |> Result.mapError (fun errors ->
                    if Directory.Exists staging then Directory.Delete(staging, true)
                    errors)
        written
        |> Result.bind (fun () ->
            let unpackedAll =
                Directory.GetFiles(Path.Combine(target, "index"), "*.json", SearchOption.AllDirectories)
                |> Array.sort
                |> Array.toList
                |> List.choose (fun indexFile ->
                    match PackageIndex.versions target (Path.GetFileNameWithoutExtension indexFile) with
                    | Result.Ok(file, [ version ]) ->
                        let archive = Path.GetFullPath(Path.Combine(Path.GetDirectoryName file, version.Url))
                        match unpacked store (Path.GetFileNameWithoutExtension indexFile) version.Version (File.ReadAllBytes archive) with
                        | Result.Ok() -> None
                        | Result.Error message -> Some message
                    | Result.Ok(_, listed) -> Some $"The index of the packages the compiler ships, '{target}', lists {listed.Length} versions of '{Path.GetFileNameWithoutExtension indexFile}', not one"
                    | Result.Error message -> Some message)
            match unpackedAll with
            | [] -> Result.Ok target
            | errors -> Result.Error errors)

    /// The stores this process has seeded, by the index each was seeded into.
    let private seeded = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Result<string, string list>>>()

    /// Seeds the packages the compiler ships into `store`, if it hasn't yet: packs each into the index of its own in the
    /// store's cache (`PackageStore.shippedIndex`), which another process seeding at the same time can't see half made,
    /// and unpacks into the cache each that it doesn't hold. Gives the index's directory; or why a package can't be
    /// seeded: it doesn't pack, or the cache holds another copy of its version. A store is seeded once a process, and
    /// one that couldn't be is tried again the next time.
    let seed (store: PackageStore) : Result<string, string list> =
        let key = PackageStore.shippedIndex store
        let result = seeded.GetOrAdd(key, fun _ -> lazy (seedOnce store)).Value
        if Result.isError result then seeded.TryRemove key |> ignore
        result

    /// Forgets which stores this process has seeded and the packages it has packed, so that the next seed reads the cache
    /// as a new process does.
    let internal forgetSeeding () =
        seeded.Clear()
        packedOnce.Clear()

    /// The user's store (`PackageStore.user`), which seeds the packages the compiler ships with `seed`.
    let user () : PackageStore = PackageStore.user seed

    /// A store whose cache is `cache` and whose index is `index`, which fetches from GitHub with `github` and seeds the
    /// packages the compiler ships with `seed`.
    let store (cache: string) (index: string) (github: GitHubFetcher) : PackageStore =
        { Cache = cache; Index = index; GitHub = github; Seed = seed }
