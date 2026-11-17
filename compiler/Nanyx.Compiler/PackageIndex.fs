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
      Yanked: bool
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

    /// `relative`, an address or a path, read from the file at `from`: an address or a rooted path as it is, and
    /// anything else relative to the directory `from` is in.
    let private resolve (from: string) (relative: string) =
        if isAddress relative || Path.IsPathRooted relative then relative
        elif isAddress from then Uri(Uri from, relative).ToString()
        else Path.GetFullPath(Path.Combine(Path.GetDirectoryName from, relative))

    /// The bytes at `location`, an address or a path, or why they can't be read.
    let private read (location: string) : Result<byte array, string> =
        if isAddress location then
            Downloads.get [] location |> Result.bind (function Some bytes -> Result.Ok bytes | None -> Result.Error $"there is no {location}")
        elif File.Exists location then
            try
                Result.Ok(File.ReadAllBytes location)
            with :? IOException as error ->
                Result.Error $"{location} can't be read: {error.Message}"
        else
            Result.Error $"there is no {location}"
    /// The JSON at `location`, or why it can't be read.
    let private readJson (location: string) : Result<JsonElement, string> =
        read location
        |> Result.bind (fun bytes ->
            try
                use document = JsonDocument.Parse(bytes: ReadOnlyMemory<byte>)
                Result.Ok(document.RootElement.Clone())
            with :? JsonException as error ->
                Result.Error $"{location} is not JSON: {error.Message}")

    let private property (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    /// Where the index files of the index at `index` are, from its `config.json`.
    let private indexFiles (index: string) : Result<string, string> =
        let config = if isAddress index then (if index.EndsWith "/" then index else index + "/") + "config.json" else Path.Combine(index, "config.json")
        readJson config
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
        let namespace' = name.Substring(0, max 0 (name.IndexOf '.'))
        indexFiles index
        |> Result.bind (fun files ->
            let file = resolve (files + "config.json") $"{namespace'}/{name}.json"
            match readJson file with
            | Result.Error _ when not (isAddress file) && not (File.Exists file) -> Result.Ok(file, [])
            | Result.Error message -> Result.Error message
            | Result.Ok listing ->
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
                                      Yanked = flag "yanked" entry
                                      Available = property "available" entry |> Option.forall (fun value -> value.ValueKind <> JsonValueKind.False) }
                            | _ -> None)
                        |> List.ofSeq
                    | _ -> []
                Result.Ok(file, listed |> List.sortBy _.Version))

    /// The directory `locked` is unpacked in, in the cache of `store`: downloaded from the store's index, if it isn't
    /// there yet, and checked against the lockfile's SHA-256 before it is unpacked. Or why it can't be: the index
    /// doesn't list it, its release is missing, or what is downloaded isn't what was locked. A yanked version still
    /// downloads, since a lockfile that names it keeps building.
    let download (store: PackageStore) (locked: LockedPackage) : Result<string, string> =
        let directory = PackageStore.directoryOf store locked.Name locked.Version
        let named = $"'{locked.Name}' {locked.Version}"
        if Directory.Exists directory then
            Result.Ok directory
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
                                    Result.Error $"The download of {named} isn't a package: {message}")))
