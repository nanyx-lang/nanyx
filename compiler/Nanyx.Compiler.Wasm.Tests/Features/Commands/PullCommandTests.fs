/// `nanyx pull`: a package's dependencies made available to build (#1471). Only a dependency found by its path, or a
/// package the compiler ships, can be had until packages can be downloaded.
module TranspilerWasmTests.Features.Commands.Pull

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// An app beside a library, acme.util, in a directory of its own; the app's manifest lists `dependencies`.
let private appWith (directory: TempDirectory) (dependencies: string) =
    let write (path: string) (text: string) =
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text)
    write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    write "app/app.nyxp" $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n  dependencies = [{dependencies}]\n)\n"
    write "app/main.nyx" "module main\n\n@entry\ndef main = { println(\"hi\") }\n"
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
    let app = appWith directory $"""(package = "nanyx.console.core", version = "{Toolchain.version}")"""
    let outcome = nanyxIn directory.Path [ "pull"; "app" ]
    Assert.Equal($"nanyx.console.core {Toolchain.version} (shipped with the compiler)\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A package with no dependencies says so`` () =
    use directory = new TempDirectory()
    let app = appWith directory ""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Equal("app has no dependencies\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A dependency named by its version can't be downloaded yet`` () =
    use directory = new TempDirectory()
    let app = appWith directory """(package = "acme.json", version = "1.2.0")"""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Equal(
        "error: acme.json 1.2.0 is named by its version, but packages can't be downloaded yet: give the directory it is in, with path\n",
        outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A dependency whose directory holds a package of another name is an error, as for a build`` () =
    use directory = new TempDirectory()
    let app = appWith directory """(package = "acme.tools", path = "../util")"""
    let outcome = nanyxIn app [ "pull" ]
    Assert.Contains("error NYX0023: the package in", outcome.StandardError)
    Assert.Contains("is named acme.util, not acme.tools: the names must match", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
