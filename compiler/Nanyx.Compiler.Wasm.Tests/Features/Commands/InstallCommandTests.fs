/// `nanyx install`: a dependency added to the manifest of the package the current directory is in (#1471), and, for one
/// from the package index, locked and downloaded (#864).
module TranspilerWasmTests.Features.Commands.Install

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// An app whose manifest is `manifest`, beside a library, acme.util, in a directory of its own.
let private appWith (directory: TempDirectory) (manifest: string) =
    directory.Write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    directory.Write "app/app.nyxp" manifest
    directory.Write "app/main.nyx" "module main\n\nimport acme.util/strings\n\n@entry\ndef main = { println(shout(\"hi\")) }\n"
    Path.Combine(directory.Path, "app")

let private manifestOf (app: string) = File.ReadAllText(Path.Combine(app, "app.nyxp")).ReplaceLineEndings "\n"

let private plain = $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n)\n"

[<Fact>]
let ``A package in a directory is added by its path, on a line of its own, and then builds`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.util"; "--path"; "../util" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("Added acme.util to app.nyxp\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  dependencies = [(package = \"acme.util\", path = \"../util\")]\n  {consoleHostEntry}\n)\n",
        manifestOf app)
    Assert.Equal("hi!\n", (nanyxIn app [ "run" ]).StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A dependency is added after the last one, laid out as the list is`` () =
    use directory = new TempDirectory()
    let core = $"(package = \"nanyx.cli\", version = \"{Toolchain.version}\")"
    let app = appWith directory $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n  dependencies = [\n    {core}\n  ]\n)\n"
    Assert.Equal(0, (nanyxIn app [ "install"; "acme.util"; "--path"; "../util" ]).ExitCode)
    Assert.Contains($"  dependencies = [\n    {core}\n    (package = \"acme.util\", path = \"../util\")\n  ]\n", manifestOf app)

[<Fact>]
let ``A package the compiler ships is added at the compiler's version`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    Assert.Equal(0, (nanyxIn app [ "install"; "nanyx.cli" ]).ExitCode)
    Assert.Contains($"dependencies = [(package = \"nanyx.cli\", version = \"{Toolchain.version}\")]", manifestOf app)

/// A store whose cache and index are directories in `directory`, whose index lists acme.json 1.0.0 and 1.1.0, each with
/// a public module `json` whose `version` says which it is.
let private indexWithJson (directory: TempDirectory) =
    let store = { Cache = Path.Combine(directory.Path, "cache"); Index = Path.Combine(directory.Path, "index"); GitHub = GitHub.unsupported }
    for version in [ "1.0.0"; "1.1.0" ] do
        directory.Write $"json-{version}/json.nyxp" $"(package = (name = \"acme.json\", version = \"{version}\"))\n"
        directory.Write $"json-{version}/json.nyx" $"export module json\n\nexport def version = \"{version}\"\n"
        publishToIndex store (Path.Combine(directory.Path, $"json-{version}")) [] "" |> ignore
    store

[<Fact>]
let ``A package from the index is added at its newest version, locked and downloaded, and then runs`` () =
    use directory = new TempDirectory()
    let store = indexWithJson directory
    let app = appWith directory plain
    File.WriteAllText(Path.Combine(app, "main.nyx"), "module main\n\nimport acme.json/json\n\n@entry\ndef main = { println(version) }\n")
    let outcome = nanyxInStore store app [ "install"; "acme.json" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("Added acme.json to app.nyxp\nLocked acme.json 1.1.0\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("dependencies = [(package = \"acme.json\", version = \"1.1.0\")]", manifestOf app)
    Assert.True(File.Exists(Path.Combine(app, "nanyx.lock")))
    Assert.True(Directory.Exists(Path.Combine(directory.Path, "cache", "acme.json", "1.1.0")))
    Assert.Equal("1.1.0\n", (nanyxInStore store app [ "run" ]).StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A package from the index is added at the versions --version accepts`` () =
    use directory = new TempDirectory()
    let store = indexWithJson directory
    let app = appWith directory plain
    let outcome = nanyxInStore store app [ "install"; "acme.json"; "--version"; "1.0.0" ]
    Assert.Equal("Added acme.json to app.nyxp\nLocked acme.json 1.1.0\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Contains("dependencies = [(package = \"acme.json\", version = \"1.0.0\")]", manifestOf app)

[<Fact>]
let ``A package no version of which can be locked is explained, and the manifest is left as it was`` () =
    use directory = new TempDirectory()
    let store = indexWithJson directory
    let app = appWith directory plain
    let outcome = nanyxInStore store app [ "install"; "acme.json"; "--version"; "2.0" ]
    Assert.Contains("error NYX0047: Version conflict 'app'", outcome.StandardError)
    Assert.Contains("Because app depends on acme.json 2.0, which no version of acme.json in the index satisfies", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(plain, manifestOf app)
    let missing = nanyxInStore store app [ "install"; "acme.nope" ]
    Assert.Contains("has no version of 'acme.nope' to install", missing.StandardError)
    Assert.Equal(plain, manifestOf app)

[<Fact>]
let ``A package already listed is an error naming its entry`` () =
    use directory = new TempDirectory()
    let app = appWith directory $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  dependencies = [(package = \"acme.util\", path = \"../util\")]\n  {consoleHostEntry}\n)\n"
    let before = manifestOf app
    let outcome = nanyxIn app [ "install"; "acme.util"; "--path"; "../util" ]
    Assert.Equal(
        "error: 'acme.util' is already a dependency, at line 3 of 'app.nyxp': `(package = \"acme.util\", path = \"../util\")`\n",
        outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(before, manifestOf app)

[<Fact>]
let ``A directory holding a package of another name is an error`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.tools"; "--path"; "../util" ]
    Assert.Contains("is named 'acme.util', not 'acme.tools': make the names match", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(plain, manifestOf app)

[<Fact>]
let ``A version that isn't one is an error, and the manifest is left as it was`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.json"; "--version"; "1.x" ]
    Assert.Equal("error: '1.x' is not a version, which must be a version, `1.2.0`, or its first numbers, `1.2`\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(plain, manifestOf app)
[<Fact>]
let ``A directory with two manifests is an error naming both`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    File.WriteAllText(Path.Combine(directory.Path, "util", "other.nyxp"), "(package = (name = \"acme.other\", version = \"0.1.0\"))\n")
    let outcome = nanyxIn app [ "install"; "acme.util"; "--path"; "../util" ]
    Assert.Contains("has more than one .nyxp manifest: 'other.nyxp' and 'util.nyxp'", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(plain, manifestOf app)