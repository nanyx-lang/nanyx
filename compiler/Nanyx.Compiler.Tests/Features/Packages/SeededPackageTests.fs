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

/// The shipped version of a package the compiler ships.
let private shippedVersion = SemanticVersion.tryParse Toolchain.version |> Option.get

/// A package that runs under the console host the compiler ships.
let private demoUnderHost () =
    new TempPackage(
        [ "demo/demo.nyxp", $"""(package = (name = "demo", version = "0.1.0"), {consoleHostEntry})"""
          "demo/src/main.nyx", "module main\n\n@entry\ndef main = {\n  println(\"Hello, World!\")\n}\n" ])

/// Makes the copy of the console host in the cache of `store` another than the compiler ships, as a compiler of the
/// same version built from other sources leaves it, and has the next seed read the cache as a new process does.
let private staleHost (store: PackageStore) =
    (okOf (SeededPackages.seed store)).Index |> ignore
    let cached = PackageStore.directoryOf store Hosts.consolePackage shippedVersion
    let file = Directory.GetFiles(Path.Combine(cached, "src"), "*.nyx") |> Array.sort |> Array.head
    File.AppendAllText(file, "\n-- stale\n")
    SeededPackages.forgetSeeding ()
    file

[<Fact>]
let ``Seeding replaces a cache's other copy of a shipped package's version with the compiler's own, with a warning`` () =
    use package = new TempPackage([])
    let store = storeOf package
    let cached = PackageStore.directoryOf store "nanyx.cli" shippedVersion
    let manifest = Path.Combine(cached, "nanyx.cli" + Manifest.extension)
    Directory.CreateDirectory cached |> ignore
    File.WriteAllText(manifest, "(package = (name = \"nanyx.cli\", version = \"0.0.0\"))\n")
    let seeded = okOf (SeededPackages.seed store)
    Assert.Equal<string list>([ "nanyx.cli" ], seeded.Replaced |> List.map _.Name)
    Assert.Equal(Toolchain.version, (Manifest.tryReadIn cached).Value.Version)
    let warning = List.exactlyOne (PackageIndex.shippedReplaced store)
    Assert.Equal(DiagnosticCodes.ShippedPackageReplaced.Code, warning.Code)
    Assert.Equal(Severity.Warning, warning.Severity)
    Assert.StartsWith(
        $"The package cache held a copy of nanyx.cli {Toolchain.version}, in '{cached}', that wasn't the one this compiler ships, so the compiler replaced that stale copy with its own.",
        warning.Message
    )
    // The next process finds the compiler's copy there, and replaces nothing.
    SeededPackages.forgetSeeding ()
    Assert.Empty((okOf (SeededPackages.seed store)).Replaced)
    Assert.Empty(PackageIndex.shippedReplaced store)

[<Fact>]
let ``A package under a shipped host builds when the cache holds a stale copy of the host`` () =
    use package = demoUnderHost ()
    let store = storeOf package
    let stale = staleHost store
    let found = okOf (Package.findIn store (package.Path "demo"))
    Assert.Equal(Some(ShippedPackages.directoryOf Hosts.consolePackage), found.HostPackage |> Option.map _.Manifest.Root)
    Assert.Empty((Package.build found).Diagnostics)
    Assert.DoesNotContain("-- stale", File.ReadAllText stale)

[<Fact>]
let ``A lockfile that locks a shipped package with a stale copy's SHA-256 builds with the compiler's own copy`` () =
    use package = demoUnderHost ()
    let store = storeOf package
    let resolved =
        match Resolution.resolve store (Manifest.tryReadIn (package.Path "demo")).Value Lockfile.empty with
        | Result.Ok resolved -> resolved.Lockfile
        | Result.Error error -> failwith $"%A{error}"
    // As a compiler of the same version built from other sources locks it.
    let stale =
        { resolved with
            Packages =
                resolved.Packages
                |> List.map (fun locked -> if locked.Name = Hosts.consolePackage then { locked with Sha256 = String.replicate 64 "1" } else locked) }
    File.WriteAllText(Lockfile.pathIn (package.Path "demo"), Lockfile.write stale)
    staleHost store |> ignore
    let found = okOf (Package.findIn store (package.Path "demo"))
    Assert.Equal(Some(ShippedPackages.directoryOf Hosts.consolePackage), found.HostPackage |> Option.map _.Manifest.Root)
    Assert.Empty((Package.build found).Diagnostics)

[<Fact>]
let ``A store that can't be seeded stops a build that reads a package from the index, with seeding's message`` () =
    use package = demoUnderHost ()
    let store = { storeOf package with Seed = fun _ -> Result.Error [ "the cache is read-only" ] }
    let path = package.Path "demo/demo.nyxp"
    let diagnostic = List.exactlyOne (Package.compileManifestFrom store path (File.ReadAllText path)).Diagnostics
    Assert.Equal(DiagnosticCodes.ShippedPackagesNotSeeded.Code, diagnostic.Code)
    Assert.Equal(
        $"'{Hosts.consolePackage}' can't be read, since the packages the compiler ships can't be seeded into the package cache, '{store.Cache}': the cache is read-only",
        diagnostic.Message
    )

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
