/// The packages the compiler ships, seeded into a package store (#2701): packed as the compiler's release packs them,
/// unpacked into the cache, and listed in an index of their own beside the package index, so a build finds them offline
/// and locks them with the SHA-256 the package index records for the release's copies (see `SeededPackages`).
module Nanyx.Compiler.Tests.Features.Packages.SeededPackages

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The SHA-256 of the shipped package `name`, packed straight from the compiler's copy, as `nanyx pack` packs it.
let private packedSha256 (store: PackageStore) (name: string) =
    PackageArchive.sha256 (okOf (PackageApi.pack (okOf (Package.findIn store (ShippedPackages.directoryOf name)))))

/// The SHA-256 the listing of `store` gives each version of `name`, and its warnings' codes.
let private listedIn (store: PackageStore) (name: string) =
    let _, listed, warnings = okOf (PackageIndex.listing store name |> Result.mapError List.singleton)
    listed |> List.map (fun version -> string version.Version, version.Sha256), warnings |> List.map _.Code

/// `package`'s index, listing `name` at `version` with the SHA-256 `sha256`, as the package index records a release.
let private indexListing (package: TempPackage) (name: string) (versions: (string * string) list) =
    Directory.CreateDirectory(package.Path $"index/index/{PackageIndex.namespaceOf name}") |> ignore
    File.WriteAllText(package.Path "index/config.json", """{ "format": 1, "index": "index/" }""")
    let entries =
        versions
        |> List.map (fun (version, sha256) ->
            $$"""{ "version": "{{version}}", "url": "https://example.com/{{name}}-{{version}}.nyxpkg", "sha256": "{{sha256}}", "dependencies": {}, "unlisted": false, "available": true }""")
    File.WriteAllText(package.Path $"index/index/{PackageIndex.namespaceOf name}/{name}.json", $$"""{ "name": "{{name}}", "versions": [{{String.concat ", " entries}}] }""")

[<Fact>]
let ``Seeding packs each shipped package into an index in the cache, and unpacks it there, as the release packs it`` () =
    use package = new TempPackage([])
    let store = storeOf package
    let index = (okOf (SeededPackages.seed store)).Index
    Assert.Equal(PackageStore.shippedIndex store, index)
    for name in ShippedPackages.names () do
        let version = SemanticVersion.tryParse Toolchain.version |> Option.get
        // Listed at the compiler's version, with the SHA-256 of the copy `nanyx pack` makes, which the release attaches.
        Assert.Equal<string * string>((Toolchain.version, packedSha256 store name), List.exactlyOne (fst (listedIn store name)))
        Assert.True(File.Exists(Path.Combine(PackageStore.directoryOf store name version, name + Manifest.extension)), $"{name} isn't in the cache")
    // Seeding again finds it seeded, and packs nothing.
    let written = Directory.GetLastWriteTimeUtc index
    Assert.Equal(index, (okOf (SeededPackages.seed store)).Index)
    Assert.Equal(written, Directory.GetLastWriteTimeUtc index)

[<Fact>]
let ``A seeded package is listed though the package index can't be read, so a build offline finds it`` () =
    use package = new TempPackage([])
    // An index that isn't there can't be read, as one can't be offline.
    let store = { storeOf package with Index = package.Path "unreachable" }
    (okOf (SeededPackages.seed store)).Index |> ignore
    let listed, warnings = listedIn store "nanyx.cli"
    Assert.Equal<string list>([ Toolchain.version ], listed |> List.map fst)
    Assert.Empty(warnings)

[<Fact>]
let ``A seeded package is listed with the versions the package index has of it`` () =
    use package = new TempPackage([])
    let store = storeOf package
    (okOf (SeededPackages.seed store)).Index |> ignore
    let seeded = packedSha256 store "nanyx.cli"
    indexListing package "nanyx.cli" [ "0.1.0", String.replicate 64 "a"; Toolchain.version, seeded ]
    Assert.Equal<(string * string) list * string list>(([ "0.1.0", String.replicate 64 "a"; Toolchain.version, seeded ], []), listedIn store "nanyx.cli")
    // A package the compiler doesn't ship is the index's alone.
    Assert.Equal<(string * string) list * string list>(([], []), listedIn store "acme.web")

[<Fact>]
let ``Where the package index records the shipped version with another SHA-256, the seeded one is listed, with a warning`` () =
    use package = new TempPackage([])
    let store = storeOf package
    (okOf (SeededPackages.seed store)).Index |> ignore
    indexListing package "nanyx.cli" [ Toolchain.version, String.replicate 64 "b" ]
    let _, listed, warnings = okOf (PackageIndex.listing store "nanyx.cli" |> Result.mapError List.singleton)
    Assert.Equal(packedSha256 store "nanyx.cli", (List.exactlyOne listed).Sha256)
    let warning = List.exactlyOne warnings
    Assert.Equal(DiagnosticCodes.ShippedPackageDiffers.Code, warning.Code)
    Assert.Equal(Severity.Warning, warning.Severity)
    let shipped, indexed = packedSha256 store "nanyx.cli", String.replicate 64 "b"
    Assert.StartsWith(
        $"This compiler's nanyx.cli {Toolchain.version} isn't the one the package index has: the copy it ships packs to the SHA-256 {shipped}, and the index records {indexed}.",
        warning.Message
    )

[<Fact>]
let ``Seeding refuses a cache that holds another copy of a shipped package's version, which it never changes`` () =
    use package = new TempPackage([])
    let store = storeOf package
    let version = SemanticVersion.tryParse Toolchain.version |> Option.get
    let cached = PackageStore.directoryOf store "nanyx.cli" version
    Directory.CreateDirectory cached |> ignore
    File.WriteAllText(Path.Combine(cached, "nanyx.cli" + Manifest.extension), "(package = (name = \"nanyx.cli\", version = \"0.0.0\"))\n")
    let error = List.exactlyOne (errorsOf (SeededPackages.seed store))
    Assert.StartsWith($"The package cache holds a 'nanyx.cli' {Toolchain.version}, in '{cached}', that isn't the one this compiler ships: its ", error)
    Assert.EndsWith("delete that directory, and the next build puts this compiler's copy there", error)

[<Fact>]
let ``Listing a package seeds the store first, as the store it was made with says`` () =
    use package = new TempPackage([])
    let store = storeOf package
    Assert.False(Directory.Exists(PackageStore.shippedIndex store))
    Assert.Equal<string list>([ Toolchain.version ], fst (listedIn store "nanyx.cli") |> List.map fst)
    Assert.True(Directory.Exists(PackageStore.shippedIndex store))

[<Fact>]
let ``A store that can't be seeded can't list a package, and says why`` () =
    use package = new TempPackage([])
    let store = { storeOf package with Seed = fun _ -> Result.Error [ "the cache is read-only" ] }
    Assert.Equal(Result.Error "the cache is read-only", PackageIndex.listing store "acme.web" |> Result.map ignore)

[<Fact>]
let ``A process that finds a cache seeded for this compiler packs nothing`` () =
    use package = new TempPackage([])
    (okOf (SeededPackages.seed (storeOf package))).Index |> ignore
    Assert.Equal(1, SeededPackages.timesWritten (storeOf package))
    // A new process, with a store of its own over the same cache, as every `nanyx` run after the first is: it would
    // pack the packages only to write the index, which it finds written.
    SeededPackages.forgetSeeding ()
    let index = (okOf (SeededPackages.seed (storeOf package))).Index
    Assert.Equal(1, SeededPackages.timesWritten (storeOf package))
    Assert.Equal(PackageStore.shippedIndex (storeOf package), index)
    Assert.Equal<string list>([ Toolchain.version ], fst (listedIn (storeOf package) "nanyx.cli") |> List.map fst)

[<Fact>]
let ``A package under a shipped host is read from the compiler's own copy, so no shipped module is checked from its source`` () =
    use package =
        new TempPackage(
            [ "demo/demo.nyxp", $"""(package = (name = "demo", version = "0.1.0"), {consoleHostEntry})"""
              "demo/src/main.nyx", "module main\n\n@entry\ndef main = {\n  println(\"Hello, World!\")\n}\n" ])
    let store = storeOf package
    let found = okOf (Package.findIn store (package.Path "demo"))
    // The host is the copy the compiler seeded, whose files are its own, not the cache's.
    Assert.Equal(Some(ShippedPackages.directoryOf Hosts.consolePackage), found.HostPackage |> Option.map _.Manifest.Root)
    let recording = Recording()
    let build = Package.buildReporting recording found
    Assert.Empty(build.Diagnostics)
    let checkedFromSource =
        recording.Events
        |> List.filter (fun event -> event.StartsWith "start " && (event.Contains ShippedPackages.directory || event.Contains store.Cache))
    Assert.Empty(checkedFromSource)

[<Fact>]
let ``A seeded host is resolved with the shipped packages it was built with, though the index has newer ones`` () =
    let newer = string (SemanticVersion.nextPatch (SemanticVersion.tryParse Toolchain.version).Value)
    let cli = $"""dependencies = [(package = "nanyx.cli", version = "{Toolchain.version}")]"""
    use package =
        new TempPackage(
            [ "app/app.nyxp", $"""(package = (name = "app", version = "0.1.0"), {consoleHostEntry}, {cli})"""
              "library/library.nyxp", $"""(package = (name = "library", version = "0.1.0"), {cli})""" ])
    let store = storeOf package
    // As the index is once the next release's libraries are on it, which this compiler, building with their `nanyx`, can
    // choose; its host, which builds with that compiler only, it can't.
    for name in [ "nanyx.cli"; "nanyx.fs"; "nanyx.http"; "nanyx.wasi" ] do
        indexListing package name [ newer, String.replicate 64 "c" ]
    let lockedOf (directory: string) =
        match Resolution.resolve store (Manifest.tryReadIn (package.Path directory)).Value Lockfile.empty with
        | Result.Ok resolved -> resolved.Lockfile.Packages |> List.map (fun locked -> locked.Name, string locked.Version)
        | Result.Error error -> failwith $"%A{error}"
    // A program under the host reads the libraries the host was built with, which define the types it runs them with.
    Assert.Equal<(string * string) list>(
        [ for name in [ "nanyx.cli"; Hosts.consolePackage; "nanyx.fs"; "nanyx.http"; "nanyx.wasi" ] -> name, Toolchain.version ]
        |> List.sort,
        lockedOf "app" |> List.sort)
    // A library, which no host runs, takes the newest.
    Assert.Equal<(string * string) list>([ "nanyx.cli", newer ], lockedOf "library")
