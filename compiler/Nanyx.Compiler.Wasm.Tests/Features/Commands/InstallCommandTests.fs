/// `nanyx install`: a dependency added to the manifest of the package the current directory is in (#1471).
module TranspilerWasmTests.Features.Commands.Install

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// An app whose manifest is `manifest`, beside a library, acme.util, in a directory of its own.
let private appWith (directory: TempDirectory) (manifest: string) =
    let write (path: string) (text: string) =
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")
    write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    write "app/app.nyxp" manifest
    write "app/main.nyx" "module main\n\nimport acme.util/strings\n\n@entry\ndef main = { println(shout(\"hi\")) }\n"
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
    let core = $"(package = \"nanyx.console.core\", version = \"{Toolchain.version}\")"
    let app = appWith directory $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n  dependencies = [\n    {core}\n  ]\n)\n"
    Assert.Equal(0, (nanyxIn app [ "install"; "acme.util"; "--path"; "../util" ]).ExitCode)
    Assert.Contains($"  dependencies = [\n    {core}\n    (package = \"acme.util\", path = \"../util\")\n  ]\n", manifestOf app)

[<Fact>]
let ``A package the compiler ships is added at the compiler's version`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    Assert.Equal(0, (nanyxIn app [ "install"; "nanyx.console.core" ]).ExitCode)
    Assert.Contains($"dependencies = [(package = \"nanyx.console.core\", version = \"{Toolchain.version}\")]", manifestOf app)

[<Fact>]
let ``A package named by its version is written, but can't be downloaded yet`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.json"; "--version"; "1.2.0" ]
    Assert.Equal(
        "error: 'acme.json' 1.2.0 is named by its version, but it isn't locked in 'nanyx.lock', and choosing a version to lock isn't supported yet: give the directory it is in, with `path`\n",
        outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("dependencies = [(package = \"acme.json\", version = \"1.2.0\")]", manifestOf app)

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
let ``A package that isn't shipped needs a path or a version`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.util" ]
    Assert.Equal("error: Neither `--path` nor `--version` was given: give the directory 'acme.util' is in with `--path`, or its version with `--version`\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A version that isn't one is an error, and the manifest is left as it was`` () =
    use directory = new TempDirectory()
    let app = appWith directory plain
    let outcome = nanyxIn app [ "install"; "acme.json"; "--version"; "1.x" ]
    Assert.Equal("error: '1.x' is not a version, which must be a version, `1.2.0`, or its first numbers, `1.2`\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(plain, manifestOf app)