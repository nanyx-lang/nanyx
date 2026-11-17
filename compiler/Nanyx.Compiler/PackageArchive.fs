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
            |> List.filter (fun dependency -> match dependency.Source with AtPath _ -> true | Shipped | FromIndex _ | OnGitHub _ -> false)
            |> List.map (fun dependency ->
                Diagnostics.at
                    DiagnosticCodes.InvalidManifestValue
                    dependency.SourceSpan
                    $"The dependency '{dependency.Name}' is found by its path, so '{manifest.Name}' can't be packed, since the directory isn't there once the package is unpacked elsewhere: depend on a version of it, `version = \"1.0\"`")
        byPath @ Manifest.publishErrors manifest

    /// When every entry of a packed package was written: the earliest time a zip can hold, so it says nothing about the
    /// machine that packed it.
    let private entryTime = DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

    /// `package`, packed: its manifest, its source files, and `extra`, files made for the package, each a path at the
    /// zip's root and its bytes, such as its `api.json` (see `PackageApi.pack`). Or what keeps it from being packed (see
    /// `packErrors`).
    let packWith (extra: (string * byte array) list) (package: Package) : Result<byte array, Diagnostic list> =
        match packErrors package.Manifest with
        | _ :: _ as errors -> Result.Error errors
        | [] ->
            let root = package.Manifest.Root
            let entries =
                (package.Manifest.Path :: package.Sources
                 |> List.map (fun file -> PackageModules.relativePath root file, lazy File.ReadAllBytes file))
                @ (extra |> List.map (fun (name, bytes) -> name, lazy bytes))
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            use stream = new MemoryStream()
            (
                use zip = new ZipArchive(stream, ZipArchiveMode.Create, true)
                for name, bytes in entries do
                    let entry = zip.CreateEntry(name, CompressionLevel.Optimal)
                    entry.LastWriteTime <- entryTime
                    use target = entry.Open()
                    target.Write(bytes.Value)
            )
            Result.Ok(stream.ToArray())

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