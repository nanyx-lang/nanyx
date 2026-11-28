namespace NyxCompiler

open System
open System.IO
open System.Text.Json

/// A version of a package as the index lists it.
type IndexedVersion =
    { Version: SemanticVersion
      /// Where its `.nyxpkg` is downloaded from: an address, or a path relative to the package's index file.
      Url: string
      /// The SHA-256 of its `.nyxpkg`, as the index first recorded it.
      Sha256: string
      /// The oldest compiler it builds with, as its manifest writes it, `0.14`, if it gives one.
      Nanyx: string option
      /// Its dependencies, each a package's name and the versions it accepts, as the index writes them: `2.1` or `^2.1`.
      Dependencies: (string * string) list
      /// Whether its author unlisted it, so that no new resolution chooses it. Older index files write this `yanked`.
      Unlisted: bool
      /// Whether its release and asset are there to download.
      Available: bool }

/// Reading the package index and downloading packages into the cache. The index's format is the README of
/// [nanyx-lang/packages](https://github.com/nanyx-lang/packages): a `config.json` that says where the index files
/// are, and one file per package listing its versions. An index is an https address, or a directory on disk, which
/// tests use.
module PackageIndex =
    /// The format of the index this compiler reads.
    let format = 1

    let private isAddress = Downloads.isAddress

    /// The namespace of the package `name`, the directory the index keeps its files in: `acme` for `acme.web`.
    let namespaceOf (name: string) = name.Substring(0, max 0 (name.IndexOf '.'))

    /// `relative`, an address or a path, read from the file at `from`: an address or a rooted path as it is, and
    /// anything else relative to the directory `from` is in.
    let private resolve (from: string) (relative: string) =
        if isAddress relative || Path.IsPathRooted relative then relative
        elif isAddress from then Uri(Uri from, relative).ToString()
        else Path.GetFullPath(Path.Combine(Path.GetDirectoryName from, relative))

    /// The bytes at `location`, an address or a path; none when there is nothing there. Or why they can't be read.
    let private tryRead (location: string) : Result<byte array option, string> =
        if isAddress location then
            Downloads.get [] location
        elif File.Exists location then
            try
                Result.Ok(Some(File.ReadAllBytes location))
            with :? IOException as error ->
                Result.Error $"{location} can't be read: {error.Message}"
        else
            Result.Ok None

    /// The index files one operation has read, by where they are, while it runs `reading`: each is read once.
    let private session = System.Threading.AsyncLocal<Collections.Concurrent.ConcurrentDictionary<string, Result<byte array option, string>> option>()

    /// Where each index file read is, where `startCounting` asks for them.
    let private counted = System.Threading.AsyncLocal<Collections.Concurrent.ConcurrentQueue<string> option>()

    /// Runs `operation`, which reads the index, reading each index file once, as `config.json` and a package's listing
    /// are read for each package a resolution asks about and again for each it downloads (#2221). Within another
    /// operation's `reading`, it shares what that one has read.
    let reading (operation: unit -> 'T) : 'T =
        match session.Value with
        | Some _ -> operation ()
        | None ->
            session.Value <- Some(Collections.Concurrent.ConcurrentDictionary())
            try operation () finally session.Value <- None

    /// Starts listing where each index file read from here on is, in this flow of work and those it starts: for the
    /// tests that count them. Gives the list.
    let startCounting () : Collections.Concurrent.ConcurrentQueue<string> =
        let reads = Collections.Concurrent.ConcurrentQueue()
        counted.Value <- Some reads
        reads

    /// `tryRead` for an index file, `config.json` or a package's listing: once in an operation's `reading`.
    let private tryReadIndexFile (location: string) : Result<byte array option, string> =
        let readCounted location =
            counted.Value |> Option.iter (fun reads -> reads.Enqueue location)
            tryRead location
        match session.Value with
        | Some files -> files.GetOrAdd(location, readCounted)
        | None -> readCounted location

    /// The bytes at `location`, or why they can't be read, as when there is nothing there.
    let private read (location: string) : Result<byte array, string> =
        tryRead location |> Result.bind (function Some bytes -> Result.Ok bytes | None -> Result.Error $"there is no {location}")

    /// `bytes`, read from `location`, as JSON, or why they aren't.
    let private parseJson (location: string) (bytes: byte array) : Result<JsonElement, string> =
        try
            use document = JsonDocument.Parse(bytes: ReadOnlyMemory<byte>)
            Result.Ok(document.RootElement.Clone())
        with :? JsonException as error ->
            Result.Error $"{location} is not JSON: {error.Message}"

    let private property (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    /// Where the index files of the index at `index` are, from its `config.json`.
    let private indexFiles (index: string) : Result<string, string> =
        let config = if isAddress index then (if index.EndsWith "/" then index else index + "/") + "config.json" else Path.Combine(index, "config.json")
        tryReadIndexFile config
        |> Result.bind (function Some bytes -> Result.Ok bytes | None -> Result.Error $"there is no {config}")
        |> Result.bind (parseJson config)
        |> Result.bind (fun config' ->
            match property "format" config', property "index" config' with
            | Some found, _ when found.ValueKind <> JsonValueKind.Number || found.GetInt32() <> format ->
                Result.Error $"The index at '{index}' is format {found}, but this nanyx reads format {format}"
            | Some _, Some files when files.ValueKind = JsonValueKind.String ->
                let files = resolve config (files.GetString())
                Result.Ok(if isAddress files || files.EndsWith "/" then files else files + string Path.DirectorySeparatorChar)
            | _ -> Result.Error $"{config} has no format and index")

    /// The versions the index at `index` lists of the package `name`, oldest first, with where its index file is, which a
    /// version's `url` is relative to; none for a package it doesn't list. Or why they can't be read.
    let versions (index: string) (name: string) : Result<string * IndexedVersion list, string> =
        indexFiles index
        |> Result.bind (fun files ->
            let file = resolve (files + "config.json") $"{namespaceOf name}/{name}.json"
            // A package the index doesn't list has no index file, on disk or at an address (404).
            match tryReadIndexFile file |> Result.bind (Option.map (parseJson file >> Result.map Some) >> Option.defaultValue (Result.Ok None)) with
            | Result.Ok None -> Result.Ok(file, [])
            | Result.Error message -> Result.Error message
            | Result.Ok(Some listing) ->
                let text name' (element: JsonElement) = property name' element |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String) |> Option.map _.GetString()
                let flag name' (element: JsonElement) = property name' element |> Option.exists (fun value -> value.ValueKind = JsonValueKind.True)
                let listed =
                    match property "versions" listing with
                    | Some versions when versions.ValueKind = JsonValueKind.Array ->
                        versions.EnumerateArray()
                        |> Seq.choose (fun entry ->
                            match text "version" entry |> Option.bind SemanticVersion.tryParse, text "url" entry, text "sha256" entry with
                            | Some version, Some url, Some sha256 ->
                                Some
                                    { Version = version
                                      Url = url
                                      Sha256 = sha256
                                      Nanyx = text "nanyx" entry
                                      Dependencies =
                                        match property "dependencies" entry with
                                        | Some dependencies when dependencies.ValueKind = JsonValueKind.Object ->
                                            dependencies.EnumerateObject()
                                            |> Seq.map (fun dependency ->
                                                dependency.Name,
                                                (if dependency.Value.ValueKind = JsonValueKind.String then dependency.Value.GetString() else string dependency.Value))
                                            |> List.ofSeq
                                        | _ -> []
                                      Unlisted = flag "unlisted" entry || flag "yanked" entry
                                      Available = property "available" entry |> Option.forall (fun value -> value.ValueKind <> JsonValueKind.False) }
                            | _ -> None)
                        |> List.ofSeq
                    | _ -> []
                Result.Ok(file, listed |> List.sortBy _.Version))

    /// The warning (`NYX0303`) that the copy of `name` at `version` the compiler ships packs to `shipped`, and the index
    /// records it with the SHA-256 `indexed`.
    let private shippedDiffers (name: string) (version: SemanticVersion) (shipped: string) (indexed: string) : Diagnostic =
        Diagnostics.general
            DiagnosticCodes.ShippedPackageDiffers
            $"This compiler's {name} {version} isn't the one the package index has: the copy it ships packs to the SHA-256 {shipped}, and the index records {indexed}. It builds with its own copy, which a lockfile it writes locks, and a download of {name} {version} won't match that. A compiler built from source whose packages/ have changed since its release does this; a released compiler doesn't, so report it if yours does"
        |> Diagnostics.about $"{name} {version}"

    /// The warnings (`NYX0308`) that seeding `store` replaced the stale copy the cache held of a package the compiler
    /// ships, one for each package, or none if it replaced none or couldn't seed.
    let shippedReplaced (store: PackageStore) : Diagnostic list =
        match store.Seed store with
        | Result.Ok seeded ->
            seeded.Replaced
            |> List.map (fun copy ->
                Diagnostics.general
                    DiagnosticCodes.ShippedPackageReplaced
                    $"The package cache held a copy of {copy.Name} {copy.Version}, in '{PackageStore.directoryOf store copy.Name copy.Version}', that wasn't the one this compiler ships, so the compiler replaced that stale copy with its own. A compiler of the same version built from other sources, as one built from source whose packages/ have changed since, leaves such a copy behind"
                |> Diagnostics.about $"{copy.Name} {copy.Version}")
        | Result.Error _ -> []

    /// The versions of the package `name` that `store` lists, oldest first, with where the index file that lists them
    /// is, which a version's relative `url` is relative to: those its index lists and, for a package the compiler ships,
    /// the version it seeded in the store's cache, seeding it first if it hasn't (`PackageStore.Seed`), whose `url` is
    /// where it is on disk. The seeded version is listed when the index can't be read, so that a build offline finds it;
    /// when the index lists it with another SHA-256, the seeded one is the one this compiler builds with, and the warning
    /// (`NYX0303`) with the versions says so. Or why the index can't be read, for a package the compiler doesn't ship, or
    /// why the packages it ships can't be seeded.
    let listing (store: PackageStore) (name: string) : Result<string * IndexedVersion list * Diagnostic list, string> =
        store.Seed store
        |> Result.mapError (String.concat "\n")
        |> Result.bind (fun seeded ->
            let seededIndex = seeded.Index
            let seeded =
                match versions seededIndex name with
                | Result.Ok(file, listed) -> listed |> List.map (fun version -> { version with Url = resolve file version.Url })
                | Result.Error _ -> []
            match seeded, versions store.Index name with
            | [], remote -> remote |> Result.map (fun (file, listed) -> file, listed, [])
            | seeded, Result.Error _ -> Result.Ok(seededIndex, seeded, [])
            | seeded, Result.Ok(file, remote) ->
                let differing =
                    seeded
                    |> List.choose (fun shipped ->
                        remote
                        |> List.tryFind (fun indexed -> indexed.Version = shipped.Version && indexed.Sha256 <> shipped.Sha256)
                        |> Option.map (fun indexed -> shippedDiffers name shipped.Version shipped.Sha256 indexed.Sha256))
                let isSeeded (version: IndexedVersion) = seeded |> List.exists (fun shipped -> shipped.Version = version.Version)
                Result.Ok(file, remote |> List.filter (isSeeded >> not) |> List.append seeded |> List.sortBy _.Version, differing))

    /// The directory `locked` is unpacked in, in the cache of `store`: downloaded from the store's index, if it isn't
    /// there yet, and checked against the lockfile's SHA-256 before it is unpacked. Or why it can't be: the index
    /// doesn't list it, its release is missing, or what is downloaded isn't what was locked. An unlisted version still
    /// downloads, since a lockfile that names it keeps building. With the directory is the version as the index lists
    /// it, when the index was read; none when the cache held it already.
    let fetch (store: PackageStore) (locked: LockedPackage) : Result<string * IndexedVersion option, string> =
        let directory = PackageStore.directoryOf store locked.Name locked.Version
        let named = $"'{locked.Name}' {locked.Version}"
        if Directory.Exists directory then
            Result.Ok(directory, None)
        else
            versions store.Index locked.Name
            |> Result.bind (fun (file, listed) ->
                match listed |> List.tryFind (fun version -> version.Version = locked.Version) with
                | None -> Result.Error $"The index at '{store.Index}' has no {named}"
                | Some version when not version.Available -> Result.Error $"{named} can't be downloaded, since its release is missing from GitHub"
                | Some version ->
                    read (resolve file version.Url)
                    |> Result.mapError (fun message -> $"{named} can't be downloaded: {message}")
                    |> Result.bind (fun bytes ->
                        let hash = PackageArchive.sha256 bytes
                        if hash <> locked.Sha256 then
                            Result.Error $"The download of {named} has the SHA-256 {hash}, but '{Lockfile.fileName}' locks {locked.Sha256}, so it was changed after it was locked and isn't used"
                        else
                            PackageArchive.unpack bytes directory
                            |> Result.mapError (fun message -> $"The download of {named} can't be unpacked: {message}")
                            |> Result.bind (fun () ->
                                // What was unpacked must be the package it was downloaded as.
                                match Manifest.manifestIn directory with
                                | Result.Ok manifestPath ->
                                    match Manifest.read manifestPath with
                                    | Result.Ok manifest when manifest.Name = locked.Name && manifest.Version = string locked.Version -> Result.Ok directory
                                    | found ->
                                        Directory.Delete(directory, true)
                                        let what = match found with Result.Ok manifest -> $"{manifest.Name} {manifest.Version}" | Result.Error _ -> "a manifest that doesn't read"
                                        Result.Error $"The download of {named} holds {what}"
                                | Result.Error message ->
                                    Directory.Delete(directory, true)
                                    Result.Error $"The download of {named} isn't a package: {message}"))
                    |> Result.map (fun directory -> directory, Some version))

    /// The directory `locked` is unpacked in, in the cache of `store`, downloaded if it isn't there yet (see `fetch`).
    let download (store: PackageStore) (locked: LockedPackage) : Result<string, string> =
        fetch store locked |> Result.map fst
