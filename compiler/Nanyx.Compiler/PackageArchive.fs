namespace NyxCompiler

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography

/// The `.nyxpkg` format, a package as it is published and downloaded: a zip of its manifest, at the zip's root, and the
/// source files its `sources` select, at their paths relative to it. Packing is deterministic, whatever machine packs:
/// each file is written as UTF-8 with LF line endings and no byte-order mark, the entries are in order of their paths,
/// stored rather than compressed, and they all have the same time, permissions and platform, so the same commit packs to
/// the same bytes, and the same SHA-256, from a Windows checkout with CRLF line endings as from a Linux one. Stored,
/// because deflate's output differs between builds of .NET and of zlib: a compiler's release and the same compiler built
/// elsewhere would pack the same files to different bytes.
module PackageArchive =
    /// A packed package's file extension.
    let extension = ".nyxpkg"

    /// The name a packed package's file has: `acme.web-1.2.0.nyxpkg`, which a release's asset must have.
    let fileName (manifest: Manifest) = $"{manifest.Name}-{manifest.Version}{extension}"

    /// The SHA-256 of `bytes`, in lower-case hexadecimal, as the index and the lockfile record it.
    let sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// An error at each dependency of `manifest` that would not be there once the package is unpacked elsewhere: one
    /// found by its path, and one on GitHub (see `Manifest.publishErrors`). A packed package depends only on packages from
    /// the index, the packages the compiler ships among them.
    let packErrors (manifest: Manifest) : Diagnostic list =
        let byPath =
            manifest.Dependencies
            |> List.filter (fun dependency -> match dependency.Source with AtPath _ -> true | FromIndex _ | OnGitHub _ -> false)
            |> List.map (fun dependency ->
                Diagnostics.at
                    DiagnosticCodes.InvalidManifestValue
                    dependency.SourceSpan
                    $"The dependency '{dependency.Name}' is found by its path, so '{manifest.Name}' can't be packed, since the directory isn't there once the package is unpacked elsewhere: depend on a version of it, `version = \"1.0\"`")
        byPath @ Manifest.publishErrors manifest

    /// When every entry of a packed package was written: the earliest time a zip can hold, so it says nothing about the
    /// machine that packed it.
    let private entryTime = DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

    /// The attributes every entry of a packed package has: a regular file that its owner can write and anyone can read
    /// (`0644`), as Unix records them in the high 16 bits.
    let private entryAttributes = int (0o100644u <<< 16)

    /// The text of a package's file, `bytes`, as a packed package holds it: UTF-8 with no byte-order mark, each CRLF
    /// line ending written as LF. None if `bytes` isn't UTF-8.
    let private normalizedText (bytes: byte array) : byte array option =
        let utf8 = Text.UTF8Encoding(false, true)
        try
            let text = utf8.GetString bytes
            let text = if text.StartsWith '\uFEFF' then text.Substring 1 else text
            Some(utf8.GetBytes(text.Replace("\r\n", "\n")))
        with :? Text.DecoderFallbackException -> None

    /// `zip`, a zip `ZipArchive` wrote, with each entry in its central directory recorded as made on Unix, the platform
    /// `entryAttributes` are written for. `ZipArchive` records the platform it runs on, and has no way to set another.
    let private madeOnUnix (zip: byte array) : byte array =
        let uint16At offset = int (BitConverter.ToUInt16(zip, offset))
        // The end of central directory record: 22 bytes at the end of a zip with no comment.
        let ending = zip.Length - 22
        let mutable entry = int (BitConverter.ToUInt32(zip, ending + 16))
        for _ in 1 .. uint16At (ending + 10) do
            // "Version made by" is 2 bytes after the central directory header's signature: the zip specification's
            // version, then the platform, 3 for Unix.
            zip[entry + 5] <- 3uy
            entry <- entry + 46 + uint16At (entry + 28) + uint16At (entry + 30) + uint16At (entry + 32)
        zip

    /// The bytes of `package`'s manifest and each of its source files, each at its path relative to the manifest, as a
    /// packed package holds them (see `normalizedText`); or an error at each that isn't UTF-8.
    let private packedFiles (package: Package) : Result<(string * byte array) list, Diagnostic list> =
        let root = package.Manifest.Root
        let files =
            package.Manifest.Path :: package.Sources
            |> List.map (fun file -> PackageModules.relativePath root file, normalizedText (File.ReadAllBytes file))
        match files |> List.filter (snd >> Option.isNone) with
        | [] -> Result.Ok(files |> List.map (fun (name, bytes) -> name, bytes.Value))
        | notText ->
            notText
            |> List.map (fun (name, _) ->
                Diagnostics.general
                    DiagnosticCodes.PackageFileNotUtf8
                    $"'{name}' isn't UTF-8 text, so '{package.Manifest.Name}' can't be packed: save it as UTF-8")
            |> Result.Error

    /// `package`, packed: its manifest, its source files, and `extra`, files made for the package, each a path at the
    /// zip's root and its bytes, such as its `api.json` (see `PackageApi.pack`), which must already be written as
    /// `normalizedText` writes a file. Or what keeps it from being packed (see `packErrors`).
    let packWith (extra: (string * byte array) list) (package: Package) : Result<byte array, Diagnostic list> =
        match packErrors package.Manifest with
        | _ :: _ as errors -> Result.Error errors
        | [] ->
            packedFiles package
            |> Result.map (fun files ->
                let entries = files @ extra |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                use stream = new MemoryStream()
                (
                    use zip = new ZipArchive(stream, ZipArchiveMode.Create, true)
                    for name, bytes in entries do
                        let entry = zip.CreateEntry(name, CompressionLevel.NoCompression)
                        entry.LastWriteTime <- entryTime
                        entry.ExternalAttributes <- entryAttributes
                        use target = entry.Open()
                        target.Write bytes
                )
                madeOnUnix (stream.ToArray()))

    /// `package`, packed with nothing but its manifest and its source files (see `packWith`).
    let pack (package: Package) : Result<byte array, Diagnostic list> = packWith [] package

    /// Unpacks `bytes`, a packed package, into `directory`, which must not exist yet: its manifest is at the zip's root,
    /// or in its one top-level folder, as a zip of a package's directory has it (see `Downloads.unzip`). Or what is
    /// wrong with it.
    let unpack (bytes: byte array) (directory: string) : Result<unit, string> =
        let isManifest (name: string) = name.EndsWith(Manifest.extension, StringComparison.OrdinalIgnoreCase)
        let prefixOf (names: string list) =
            if names |> List.exists (fun name -> isManifest name && not (name.Contains '/')) then
                Result.Ok ""
            else
                match Downloads.singleFolder names with
                | Some folder when names |> List.exists (fun name -> name.StartsWith folder && isManifest name && not (name.Substring(folder.Length).Contains '/')) ->
                    Result.Ok folder
                | _ -> Result.Error $"it has no {Manifest.extension} manifest at its root, or in its one folder"
        Downloads.unzip prefixOf bytes directory