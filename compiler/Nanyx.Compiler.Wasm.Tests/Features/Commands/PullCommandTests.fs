/// `nanyx pull`: a package's dependencies made available to build (#1471): a dependency found by its path, a package
/// the compiler ships, or one `nanyx.lock` locks, downloaded from the index into the cache (#863), whose versions are
/// chosen from the index when the manifest asks for something the lockfile doesn't lock (#864); and a build doing the
/// same, unless `--locked` refuses to.
module TranspilerWasmTests.Features.Commands.Pull

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// An app beside a library, acme.util, in a directory of its own; the app's manifest lists `dependencies`.
let private appWith (directory: TempDirectory) (dependencies: string) =
    directory.Write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    directory.Write "app/app.nyxp" $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n  dependencies = [{dependencies}]\n)\n"
    directory.Write "app/main.nyx" "module main\n\n@entry\ndef main = { println(\"hi\") }\n"
    Path.Combine(directory.Path, "app")

[<Fact>]
let ``A path dependency is checked and listed with its version and directory`` () =
    use directory = new TempDirectory()
    let app = appWith directory """(package = "acme.util", path = "../util")"""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("acme.util 0.3.0 (../util)\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A package the compiler ships is listed as shipped with it`` () =
    use directory = new TempDirectory()
    let app = appWith directory $"""(package = "nanyx.cli", version = "{Toolchain.version}")"""
    let outcome = nanyxIn directory.Path [ "pull"; "app" ]
    Assert.Equal($"nanyx.cli {Toolchain.version} (shipped with the compiler)\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A package with no dependencies says so`` () =
    use directory = new TempDirectory()
    let app = appWith directory ""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Equal("app has no dependencies\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``Dependencies no versions satisfy are explained, and nothing is written`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 42 ""
    directory.Write "web/web.nyxp" "(package = (name = \"acme.web\", version = \"1.0.0\"), dependencies = [(package = \"acme.core\", version = \"1.0\")])\n"
    directory.Write "web/web.nyx" "export module web\n\nexport def page = 1\n"
    publishToIndex store (Path.Combine(directory.Path, "web")) [ "acme.core", "1.0" ] "" |> ignore
    publishCore directory store "2.0.0" 42 ""
    let app = appWith directory """(package = "acme.web", version = "1.0"), (package = "acme.core", version = "2.0")"""
    let outcome = nanyxInStore store app [ "pull" ]
    Assert.Contains("error NYX0047: Version conflict 'app'", outcome.StandardError)
    Assert.Contains(
        "Because acme.web 1.0.0 depends on acme.core 1.0 and app depends on acme.web 1.0, acme.core 1.0.0 is required.",
        outcome.StandardError)
    Assert.Contains("And because app depends on acme.core 2.0, the dependencies can't all be satisfied.", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
    Assert.False(File.Exists(Path.Combine(app, "nanyx.lock")))

[<Fact>]
let ``An unlisted version is passed over for a new lock, but kept where it is locked already`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 42 ""
    publishCore directory store "1.1.0" 42 ", \"unlisted\": true"
    let app = appWith directory """(package = "acme.core", version = "1.0")"""
    let pulled = nanyxInStore store app [ "pull" ]
    Assert.Equal("", pulled.StandardError)
    Assert.Equal("Locked acme.core 1.0.0\nacme.core 1.0.0 (downloaded)\n", pulled.StandardOutput.ReplaceLineEndings "\n")
    let hash =
        match Lockfile.read (Path.Combine(app, "nanyx.lock")) with
        | Result.Ok(Some lockfile) -> (Lockfile.tryFind "acme.core" lockfile).Value.Sha256
        | other -> failwith $"{other}"
    let unlisted = File.ReadAllBytes(Path.Combine(directory.Path, "index", "artifacts", "acme.core-1.1.0.nyxpkg")) |> PackageArchive.sha256
    File.WriteAllText(
        Path.Combine(app, "nanyx.lock"),
        Lockfile.write { Packages = [ { Name = "acme.core"; Version = (SemanticVersion.tryParse "1.1.0").Value; Sha256 = unlisted; Dependencies = [] } ] })
    Assert.NotEqual<string>(hash, unlisted)
    let kept = nanyxInStore store app [ "pull" ]
    Assert.StartsWith("warning NYX0051: Unlisted version 'acme.core 1.1.0'", kept.StandardError)
    Assert.Equal("acme.core 1.1.0 (downloaded)\n", kept.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A build locks what the manifest adds, and with --locked refuses a lockfile that doesn't lock it`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 42 ""
    let app = appWith directory ""
    Assert.Equal(0, (nanyxInStore store app [ "pull" ]).ExitCode)
    File.WriteAllText(Path.Combine(app, "app.nyxp"), (File.ReadAllText(Path.Combine(app, "app.nyxp"))).Replace("dependencies = []", "dependencies = [(package = \"acme.core\", version = \"1.0\")]"))
    let refused = nanyxInStore store app [ "build"; "--locked" ]
    Assert.Equal(
        "error: 'nanyx.lock' doesn't satisfy what 'app.nyxp' asks for, and `--locked` keeps it as it is: run `nanyx pull` to update it\n"
        + "error: 'app' depends on 'acme.core' 1.0, which it doesn't lock\n",
        refused.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, refused.ExitCode)
    Assert.False(File.Exists(Path.Combine(app, "nanyx.lock")))
    let built = nanyxInStore store app [ "build" ]
    Assert.Equal("Locked acme.core 1.0.0\n", built.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(0, built.ExitCode)
    Assert.Equal(0, (nanyxInStore store app [ "build"; "--locked" ]).ExitCode)

[<Fact>]
let ``A dependency whose directory holds a package of another name is an error, as for a build`` () =
    use directory = new TempDirectory()
    let app = appWith directory """(package = "acme.tools", path = "../util")"""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Contains("error NYX0023: The package in", outcome.StandardError)
    Assert.Contains("is named 'acme.util', not 'acme.tools': make the names match", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A locked dependency is downloaded from the index into the cache, and the package runs with it`` () =
    use directory = new TempDirectory()
    let app = appWith directory """(package = "acme.util", version = "0.3")"""
    File.WriteAllText(Path.Combine(app, "main.nyx"), "module main\n\nimport acme.util/strings\n\n@entry\ndef main = { println(shout(\"hi\")) }\n")
    let store = storeIn directory
    let hash = publishToIndex store (Path.Combine(directory.Path, "util")) [] ""
    File.WriteAllText(
        Path.Combine(app, "nanyx.lock"),
        Lockfile.write { Packages = [ { Name = "acme.util"; Version = (SemanticVersion.tryParse "0.3.0").Value; Sha256 = hash; Dependencies = [] } ] }
    )
    let pulled = nanyxInStore store app [ "pull" ]
    Assert.Equal("", pulled.StandardError)
    Assert.Equal("acme.util 0.3.0 (downloaded)\n", pulled.StandardOutput.ReplaceLineEndings "\n")
    assertRan "hi!\n" (nanyxInStore store app [ "run" ])