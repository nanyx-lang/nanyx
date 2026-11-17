module Nanyx.Compiler.Tests.Features.Packages.LockfileAndCache

open System.IO
open System.IO.Compression
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A dependency named by its version is the version `nanyx.lock` locks, downloaded from the package index as a `.nyxpkg`,
// checked against the lockfile's SHA-256 and unpacked into the per-user cache (#863). The index here is a directory on
// disk, in the format of nanyx-lang/packages.

let private version text = (SemanticVersion.tryParse text).Value

let private requirement text = (VersionRequirement.tryParse text).Value

[<Fact>]
let ``Versions are ordered as semantic versioning orders them`` () =
    let ordered = [ "0.9.0"; "1.0.0-alpha"; "1.0.0-alpha.1"; "1.0.0-alpha.beta"; "1.0.0-beta.2"; "1.0.0-beta.11"; "1.0.0"; "1.2.0"; "1.10.0"; "2.0.0" ]
    Assert.Equal<string list>(ordered, ordered |> List.rev |> List.map version |> List.sort |> List.map string)
    Assert.Equal(version "1.2.0", version "1.2.0+build.5")
    Assert.Equal(None, SemanticVersion.tryParse "1.2")
    Assert.Equal(None, SemanticVersion.tryParse "01.2.0")

[<Fact>]
let ``A requirement accepts the versions compatible with it`` () =
    let accepts written candidate = VersionRequirement.accepts (requirement written) (version candidate)
    Assert.True(accepts "1.2" "1.2.0")
    Assert.True(accepts "1.2" "1.9.3")
    Assert.False(accepts "1.2" "1.1.9")
    Assert.False(accepts "1.2" "2.0.0")
    Assert.True(accepts "0.3" "0.3.7")
    Assert.False(accepts "0.3" "0.4.0")
    Assert.False(accepts "1.2" "1.3.0-beta.1")
    Assert.True(accepts "1.3.0-beta.1" "1.3.0-beta.2")
    Assert.Equal(None, VersionRequirement.tryParse "^1.2")

let private hashOf (letter: char) = String.replicate 64 (string letter)

let private lockfile =
    { Packages =
        [ { Name = "acme.web"; Version = version "1.2.0"; Sha256 = hashOf 'b'; Dependencies = [ "acme.core" ] }
          { Name = "acme.core"; Version = version "2.3.1"; Sha256 = hashOf 'a'; Dependencies = [] } ] }

[<Fact>]
let ``A lockfile is written sorted by name, and read back`` () =
    let written = Lockfile.write lockfile
    let expected =
        $"""-- Written by nanyx, which keeps it up to date: don't edit it by hand.
(
  version = 1
  packages = [
    (package = "acme.core", version = "2.3.1", sha256 = "{hashOf 'a'}", dependencies = [])
    (package = "acme.web", version = "1.2.0", sha256 = "{hashOf 'b'}", dependencies = ["acme.core"])
  ]
)
"""
    Assert.Equal(expected.ReplaceLineEndings "\n", written)
    let read = okOf (Lockfile.check "nanyx.lock" written)
    Assert.Equal<string list>([ "acme.core"; "acme.web" ], read.Packages |> List.map _.Name)
    Assert.Equal(version "1.2.0", (Lockfile.tryFind "acme.web" read).Value.Version)

[<Fact>]
let ``A lockfile with a package locked twice, or a hash that isn't one, is refused`` () =
    let twice = (Lockfile.write lockfile).Replace("\"acme.web\"", "\"acme.core\"")
    Assert.Contains(errorsOf (Lockfile.check "nanyx.lock" twice), fun error -> error.Contains "locks 'acme.core' 2 times")
    let badHash = (Lockfile.write lockfile).Replace(hashOf 'a', "abc")
    Assert.Contains(errorsOf (Lockfile.check "nanyx.lock" badHash), fun error -> error.Contains "is not 64 hexadecimal digits")
    let format = (Lockfile.write lockfile).Replace("version = 1", "version = 2")
    Assert.Contains(errorsOf (Lockfile.check "nanyx.lock" format), fun error -> error.Contains "this nanyx reads format 1")

/// The files of the package `name` at `version` in the directory `directory`: its manifest, depending on
/// `dependencies` as the manifest writes them, and one public module, `strings`, whose `shout` adds `mark`.
let private library (directory: string) (name: string) (version: string) (dependencies: string list) (body: string) =
    let entries = String.concat ", " dependencies
    [ $"{directory}/{name}.nyxp", $"(package = (name = \"{name}\", version = \"{version}\"), dependencies = [{entries}])\n"
      $"{directory}/strings.nyx", withFinalNewline body ]

let private util =
    library "util" "acme.util" "1.0.0" [] """export module strings

export def shout = { s: string -> s ++ "!" }"""

let private app (dependencies: string list) =
    let entries = String.concat ", " dependencies
    [ "app/app.nyxp",
      withFinalNewline $"""(
  package = (name = "app", version = "0.1.0")
  dependencies = [{entries}]
  {consoleHostEntry}
)"""
      "app/main.nyx",
      withFinalNewline """module main

import acme.util/strings

@entry
def run = { () -> ignore(shout("hi")) }""" ]

/// Packs the package whose manifest is at `manifest` into the index's `artifacts` directory, lists it in the index, and
/// gives its locked entry.
let private publish (package: TempPackage) (manifest: string) : LockedPackage =
    let found = okOf (Package.ofManifestFrom (storeOf package) (package.Path manifest))
    let hash = publishToIndex package found (okOf (PackageArchive.pack found))
    { Name = found.Manifest.Name
      Version = version found.Manifest.Version
      Sha256 = hash
      Dependencies = found.Dependencies |> List.map _.Manifest.Name }

let private lock (package: TempPackage) (locked: LockedPackage list) =
    File.WriteAllText(package.Path "app/nanyx.lock", Lockfile.write { Packages = locked })

[<Fact>]
let ``Packing the same package twice makes the same bytes, of its manifest and its sources`` () =
    use package = new TempPackage(util)
    let found = okOf (Package.ofManifestFrom (storeOf package) (package.Path "util/acme.util.nyxp"))
    let first = okOf (PackageArchive.pack found)
    Assert.Equal<byte array>(first, okOf (PackageArchive.pack found))
    use zip = new ZipArchive(new MemoryStream(first))
    Assert.Equal<string list>([ "acme.util.nyxp"; "strings.nyx" ], zip.Entries |> Seq.map _.FullName |> List.ofSeq)
    Assert.Equal("acme.util-1.0.0.nyxpkg", PackageArchive.fileName found.Manifest)

[<Fact>]
let ``A package with a path dependency or one on GitHub can't be packed`` () =
    let github = """(module = "retry", github = "acme/snippets/retry.nyx", rev = "1f8ae8b3c9d04e5f6a7b8c9d0e1f2a3b4c5d6e7f")"""
    let manifest = okOf (Manifest.check "app.nyxp" $"""(package = (name = "app", version = "0.1.0"), dependencies = [(package = "acme.util", path = "../util"), {github}])""")
    Assert.Equal<string list>(
        [ DiagnosticCodes.InvalidManifestValue.Code; DiagnosticCodes.GitHubReferenceNotPublishable.Code ],
        PackageArchive.packErrors manifest |> List.map _.Code
    )
    Assert.Contains("is found by its path, so 'app' can't be packed", (PackageArchive.packErrors manifest).Head.Message)

[<Fact>]
let ``Unpacking refuses an entry that would land outside the package`` () =
    use stream = new MemoryStream()
    (
        use zip = new ZipArchive(stream, ZipArchiveMode.Create, true)
        for name in [ "evil.nyxp"; "../outside.nyx" ] do
            use writer = new StreamWriter(zip.CreateEntry(name).Open())
            writer.Write "x"
    )
    use package = new TempPackage([])
    let error = errorsOf (PackageArchive.unpack (stream.ToArray()) (package.Path "cache/evil/1.0.0"))
    Assert.Contains("would be unpacked outside", error)
    Assert.False(Directory.Exists(package.Path "cache/evil/1.0.0"))

[<Fact>]
let ``Pull downloads a locked package into the cache, and the build uses it`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "1.0")""" ])
    lock package [ publish package "util/acme.util.nyxp" ]
    let store = storeOf package
    Assert.Equal<string list>([ "acme.util 1.0.0 (downloaded)" ], okOf (PackageDependencies.pull store (package.Path "app/app.nyxp")))
    Assert.True(File.Exists(package.Path "cache/acme.util/1.0.0/strings.nyx"))
    Assert.Equal<string list>([ "acme.util 1.0.0 (in the cache)" ], okOf (PackageDependencies.pull store (package.Path "app/app.nyxp")))
    let build = Package.build (okOf (Package.ofManifestFrom store (package.Path "app/app.nyxp")))
    Assert.Empty(build.Diagnostics)

[<Fact>]
let ``A package and the package it depends on are both locked and downloaded`` () =
    let web =
        library "web" "acme.web" "2.1.0" [ """(package = "acme.util", version = "1.0")""" ] """export module loud

import acme.util/strings

export def loud = { s: string -> shout(s) }"""
    use package = new TempPackage(util @ web @ app [ """(package = "acme.web", version = "2.0"), (package = "acme.util", version = "1")""" ])
    let store = storeOf package
    let utilLocked = publish package "util/acme.util.nyxp"
    // acme.web is built against the acme.util its own lockfile locks before it is packed.
    File.WriteAllText(package.Path "web/nanyx.lock", Lockfile.write { Packages = [ utilLocked ] })
    okOf (PackageDependencies.pull store (package.Path "web/acme.web.nyxp")) |> ignore
    let webLocked = publish package "web/acme.web.nyxp"
    Assert.Equal<string list>([ "acme.util" ], webLocked.Dependencies)
    lock package [ utilLocked; webLocked ]
    let pulled = okOf (PackageDependencies.pull store (package.Path "app/app.nyxp"))
    Assert.Equal<string list>([ "acme.web 2.1.0 (downloaded)"; "acme.util 1.0.0 (in the cache)" ], pulled)
    Assert.Empty((Package.build (okOf (Package.ofManifestFrom store (package.Path "app/app.nyxp")))).Diagnostics)

[<Fact>]
let ``A download whose SHA-256 isn't the locked one is refused`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "1.0")""" ])
    let locked = publish package "util/acme.util.nyxp"
    lock package [ { locked with Sha256 = hashOf 'c' } ]
    let error = errorsOf (PackageDependencies.pull (storeOf package) (package.Path "app/app.nyxp")) |> List.exactlyOne
    Assert.Contains($"but 'nanyx.lock' locks {hashOf 'c'}", error)
    Assert.False(Directory.Exists(package.Path "cache/acme.util"))

[<Fact>]
let ``A version the index doesn't list can't be downloaded`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "1.0")""" ])
    let locked = publish package "util/acme.util.nyxp"
    lock package [ { locked with Version = version "1.0.1" } ]
    let error = errorsOf (PackageDependencies.pull (storeOf package) (package.Path "app/app.nyxp")) |> List.exactlyOne
    Assert.Contains("has no 'acme.util' 1.0.1", error)

[<Fact>]
let ``A dependency the lockfile doesn't lock can't be had yet`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "1.0")""" ])
    let error = errorsOf (PackageDependencies.pull (storeOf package) (package.Path "app/app.nyxp")) |> List.exactlyOne
    Assert.Contains("isn't locked in 'nanyx.lock', and choosing a version to lock isn't supported yet", error)
    let diagnostic = (Package.compileManifestFrom (storeOf package) (package.Path "app/app.nyxp") (File.ReadAllText(package.Path "app/app.nyxp"))).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnsupportedManifestField.Code, diagnostic.Code)

[<Fact>]
let ``A locked version the dependency doesn't accept is an error at its version`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "2.0")""" ])
    lock package [ publish package "util/acme.util.nyxp" ]
    let text = File.ReadAllText(package.Path "app/app.nyxp")
    let diagnostic = (Package.compileManifestFrom (storeOf package) (package.Path "app/app.nyxp") text).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, diagnostic.Code)
    Assert.Equal("\"2.0\"", textAt (text.ReplaceLineEndings "\n") (spanOf diagnostic))
    Assert.Contains("'nanyx.lock' locks 'acme.util' 1.0.0, but this dependency accepts only", diagnostic.Message)

[<Fact>]
let ``A locked package that isn't in the cache is an error that says to pull`` () =
    use package = new TempPackage(util @ app [ """(package = "acme.util", version = "1.0")""" ])
    lock package [ publish package "util/acme.util.nyxp" ]
    let text = File.ReadAllText(package.Path "app/app.nyxp")
    let diagnostic = (Package.compileManifestFrom (storeOf package) (package.Path "app/app.nyxp") text).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.PackageNotDownloaded.Code, diagnostic.Code)
    Assert.Contains("run `nanyx pull` to download it", diagnostic.Message)

[<Fact>]
let ``A version requirement must be one`` () =
    match Manifest.check "app.nyxp" """(package = (name = "app", version = "0.1.0"), dependencies = [(package = "acme.util", version = "^1.2")])""" with
    | Result.Error [ error ] ->
        Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
        Assert.Contains("must be a version, `1.2.0`, or its first numbers, `1.2`", error.Message)
    | other -> failwith $"%A{other}"
