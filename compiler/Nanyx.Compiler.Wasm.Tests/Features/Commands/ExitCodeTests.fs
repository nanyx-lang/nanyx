module TranspilerWasmTests.Features.Commands.ExitCodes

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// Asserts that `nanyx <arguments>`, run in `directory`, exited with `expected` and wrote `message` to stderr.
let private assertExit (expected: int) (message: string) (directory: string) (arguments: string list) =
    let outcome = nanyxIn directory arguments
    Assert.Equal(expected, outcome.ExitCode)
    Assert.Equal(message, outcome.StandardError.Trim())

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("build")>]
let ``Exit codes - more than one path is a usage error, 2, for every command that takes one`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "Give one file, directory or manifest, not 2." directory.Path [ command; "a"; "b" ]

[<Fact>]
let ``Exit codes - new given two directories is a usage error, 2`` () =
    use directory = new TempDirectory()
    assertExit 2 "Give one file, directory or manifest, not 2." directory.Path [ "new"; "acme.demo"; "a"; "b" ]

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("wasm")>]
let ``Exit codes - a file that does not exist is a usage error, 2`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "File not found: missing.nyx" directory.Path [ command; "missing.nyx" ]

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("build")>]
let ``Exit codes - a manifest that does not exist is a usage error, 2`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "Not found: missing.nyxp" directory.Path [ command; "missing.nyxp" ]

[<Fact>]
let ``Exit codes - build with no manifest in or above the directory fails with 1`` () =
    use directory = new TempDirectory()
    let outcome = nanyxIn directory.Path [ "build" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.StartsWith("No .nyxp manifest in ", outcome.StandardError)

[<Fact>]
let ``Exit codes - a script with an error fails check with 1`` () =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), "def answer: int = \"forty-two\"\n")
    let outcome = nanyxIn directory.Path [ "check"; "main.nyx" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("main.nyx:1:19: error NYX0201: Type mismatch: string vs int.", outcome.StandardOutput)

[<Fact>]
let ``Exit codes - a script that checks exits with 0`` () =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), "def answer: int = 42\n")
    let outcome = nanyxIn directory.Path [ "check"; "main.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
