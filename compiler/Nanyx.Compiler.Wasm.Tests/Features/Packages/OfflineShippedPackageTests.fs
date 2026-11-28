/// The packages the compiler ships are index packages that come seeded (#2701): resolved, locked with their SHA-256s and
/// read as any package from the index is, but with no network, since the compiler seeds them into the cache itself.
module TranspilerWasmTests.Features.Packages.OfflineShippedPackages

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A store with a cache of its own and an index that can't be read, as a machine with no network has.
let private offline (directory: TempDirectory) : PackageStore =
    SeededPackages.store (Path.Combine(directory.Path, "cache")) (Path.Combine(directory.Path, "no-index")) GitHub.unsupported

/// The packages `directory`'s lockfile locks, each with the SHA-256 it locks.
let private lockedIn (directory: string) =
    match Lockfile.read (Lockfile.pathIn directory) with
    | Result.Ok(Some lockfile) -> lockfile.Packages |> List.map (fun locked -> locked.Name, string locked.Version, locked.Sha256)
    | other -> failwith $"{other}"

[<Fact>]
let ``A new package runs hello world with no network, and locks its host with the SHA-256 the compiler seeded`` () =
    use directory = new TempDirectory()
    let store = offline directory
    Assert.Equal(0, (nanyxInStore store directory.Path [ "new"; "acme.hello"; "hello" ]).ExitCode)
    let outcome = nanyxInStore store (Path.Combine(directory.Path, "hello")) [ "run" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("Hello, World!\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    let host = (PackageStore.seededCopy store Hosts.consolePackage).Value
    Assert.Contains((Hosts.consolePackage, string host.Version, host.Sha256), lockedIn (Path.Combine(directory.Path, "hello")))

[<Fact>]
let ``A package under the HTTP host builds with no network`` () =
    use directory = new TempDirectory()
    directory.Write
        "server/server.nyxp"
        $"""(package = (name = "acme.server", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "nanyx.http.host", version = "{Toolchain.version}"))"""
    directory.Write "server/src/main.nyx" "module main\n\n@entry\ndef main: Request -> Response = { _ -> Response.text(200, \"hi\") }\n"
    let outcome = nanyxInStore (offline directory) (Path.Combine(directory.Path, "server")) [ "check" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A script that names a package the compiler ships runs with no network`` () =
    use directory = new TempDirectory()
    directory.Write "script.nyx" "::install nanyx.fs\n\nimport nanyx.fs/path as path\n\nprintln(path.of(\"a/b.txt\"))\n"
    let outcome = nanyxInStore (offline directory) directory.Path [ "run"; "script.nyx" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A lockfile written before the compiler locked the packages it ships gains them on the next pull`` () =
    use directory = new TempDirectory()
    let store = offline directory
    Assert.Equal(0, (nanyxInStore store directory.Path [ "new"; "acme.hello"; "hello" ]).ExitCode)
    let package = Path.Combine(directory.Path, "hello")
    File.WriteAllText(Lockfile.pathIn package, Lockfile.write Lockfile.empty)
    Assert.Equal(0, (nanyxInStore store package [ "pull" ]).ExitCode)
    let locked = lockedIn package |> List.map (fun (name, _, _) -> name)
    Assert.Contains(Hosts.consolePackage, locked)
    Assert.Contains("nanyx.cli", locked)

[<Fact>]
let ``The tests seed the shipped packages into a package cache of their own, not the user's`` () =
    let cache = PackageStore.defaultCache ()
    let home = System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
    Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), cache)
    Assert.NotEqual<string>(Path.Combine(home, ".nanyx", "packages"), cache)
    Assert.True(Directory.Exists cache)
