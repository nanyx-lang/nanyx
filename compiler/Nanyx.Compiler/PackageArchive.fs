namespace NyxCompiler

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography

/// The `.nyxpkg` format, a package as it is published and downloaded: a zip of its manifest, at the zip's root, and the
/// source files its `sources` select, at their paths relative to it. Packing is deterministic: the entries are in
/// order of their paths and all have the same time, so packing the same files twice makes the same bytes, and the same
/// SHA-256.
module PackageArchive =
    /// A packed package's file extension.
    let extension = ".nyxpkg"

    /// The name a packed package's file has: `acme.web-1.2.0.nyxpkg`, which a release's asset must have.
    let fileName (manifest: Manifest) = $"{manifest.Name}-{manifest.Version}{extension}"

    /// The SHA-256 of `bytes`, in lower-case hexadecimal, as the index and the lockfile record it.
    let sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// An error at each dependency of `manifest` that would not be there once the package is unpacked elsewhere: one
    /// found by its path, and one on GitHub (see `Manifest.publishErrors`). A packed package depends only on packages from
    /// the index and on those the compiler ships.
    let packErrors (manifest: Manifest) : Diagnostic list =
        let byPath =
            manifest.Dependencies
            |> List.filter (fun dependency -> dependency.GitHub.IsNone && dependency.Version.IsNone && dependency.Directory <> ShippedPackages.directoryOf dependency.Name)
            |> List.map (fun dependency ->
                Diagnostics.at
                    DiagnosticCodes.InvalidManifestValue
                    dependency.PathSpan
                    $"the dependency {dependency.Name} is found by its path, so {manifest.Name} can't be packed: the directory isn't there once the package is unpacked elsewhere, so depend on a version of it, version = \"1.0\"")
        byPath @ Manifest.publishErrors manifest

    /// When every entry of a packed package was written: the earliest time a zip can hold, so it says nothing about the
    /// machine that packed it.
    let private entryTime = DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

    /// `package`, packed: its manifest and its source files. Or what keeps it from being packed (see `packErrors`).
    let pack (package: Package) : Result<byte array, Diagnostic list> =
        match packErrors package.Manifest with
        | _ :: _ as errors -> Result.Error errors
        | [] ->
            let root = package.Manifest.Root
            let entries =
                package.Manifest.Path :: package.Sources
                |> List.map (fun file -> Path.GetRelativePath(root, file).Replace('\\', '/'), file)
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            use stream = new MemoryStream()
            (
                use zip = new ZipArchive(stream, ZipArchiveMode.Create, true)
                for name, file in entries do
                    let entry = zip.CreateEntry(name, CompressionLevel.Optimal)
                    entry.LastWriteTime <- entryTime
                    use target = entry.Open()
                    target.Write(File.ReadAllBytes file)
            )
            Result.Ok(stream.ToArray())

    /// The path of `entry`, a name in a packed package, under the prefix `prefix`, relative to where it is unpacked; or
    /// none if it would land outside that directory.
    let private relative (prefix: string) (entry: string) =
        let name = entry.Replace('\\', '/')
        if not (name.StartsWith prefix) then
            None
        else
            let rest = name.Substring prefix.Length
            let parts = rest.Split '/'
            if rest = "" || Path.IsPathRooted rest || rest.StartsWith "/" || parts |> Array.exists (fun part -> part = ".." || part.Contains ':') then None
            else Some rest

    /// Unpacks `bytes`, a packed package, into `directory`, which must not exist yet: its manifest is at the zip's root,
    /// or in its one top-level folder, as a zip of a package's directory has it. It is unpacked beside `directory` first
    /// and moved there, so a package is never seen half unpacked. Or what is wrong with it.
    let unpack (bytes: byte array) (directory: string) : Result<unit, string> =
        try
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
            let files = zip.Entries |> Seq.filter (fun entry -> not (entry.FullName.EndsWith "/")) |> List.ofSeq
            let isManifest (name: string) = name.EndsWith(Manifest.extension, StringComparison.OrdinalIgnoreCase)
            let prefix =
                match files |> List.filter (fun entry -> isManifest entry.FullName && not (entry.FullName.Contains '/')) with
                | [ _ ] -> Some ""
                | _ ->
                    match files |> List.map (fun entry -> entry.FullName.Split('/')[0]) |> List.distinct with
                    | [ folder ] when files |> List.exists (fun entry -> entry.FullName = $"{folder}/{Path.GetFileName entry.FullName}" && isManifest entry.FullName) ->
                        Some(folder + "/")
                    | _ -> None
            match prefix with
            | None -> Result.Error $"it has no {Manifest.extension} manifest at its root, or in its one folder"
            | Some prefix ->
                match files |> List.tryFind (fun entry -> (relative prefix entry.FullName).IsNone) with
                | Some outside -> Result.Error $"its entry {outside.FullName} would be unpacked outside the package's directory"
                | None ->
                    let partial = $"{directory}.partial-{Guid.NewGuid():N}"
                    try
                        for entry in files do
                            let target = Path.Combine(partial, (relative prefix entry.FullName).Value)
                            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                            entry.ExtractToFile target
                        Directory.CreateDirectory(Path.GetDirectoryName directory) |> ignore
                        // Another build may have unpacked the same version meanwhile; a package in the cache is never
                        // changed, so its copy is as good as this one.
                        if not (Directory.Exists directory) then
                            Directory.Move(partial, directory)
                        Result.Ok()
                    finally
                        if Directory.Exists partial then
                            Directory.Delete(partial, true)
        with :? InvalidDataException as error ->
            Result.Error $"it is not a zip: {error.Message}"
