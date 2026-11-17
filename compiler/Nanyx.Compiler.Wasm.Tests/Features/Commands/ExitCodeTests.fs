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
[<InlineData("build")>]
let ``Exit codes - more than one path is a usage error, 2, for every command that takes one`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "2 paths were given: give one file, directory or manifest" directory.Path [ command; "a"; "b" ]

[<Fact>]
let ``Exit codes - run takes what follows the path as the program's arguments, not as more paths`` () =
    use directory = new TempDirectory()
    assertExit 2 "error NYX0002: There is no file 'a.nyx'" directory.Path [ "run"; "a.nyx"; "b" ]

[<Fact>]
let ``Exit codes - new given two directories is a usage error, 2`` () =
    use directory = new TempDirectory()
    assertExit 2 "2 paths were given: give one file, directory or manifest" directory.Path [ "new"; "acme.demo"; "a"; "b" ]

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("wasm")>]
let ``Exit codes - a file that does not exist is a usage error, 2`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "error NYX0002: There is no file 'missing.nyx'" directory.Path [ command; "missing.nyx" ]

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("build")>]
let ``Exit codes - a manifest that does not exist is a usage error, 2`` (command: string) =
    use directory = new TempDirectory()
    assertExit 2 "There is no file or directory 'missing.nyxp'" directory.Path [ command; "missing.nyxp" ]

[<Fact>]
let ``Exit codes - build with no manifest in or above the directory fails with 1`` () =
    use directory = new TempDirectory()
    let outcome = nanyxIn directory.Path [ "build" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.StartsWith("There is no .nyxp manifest in '", outcome.StandardError)

[<Fact>]
let ``Exit codes - a script with an error fails check with 1`` () =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), "def answer: int = \"forty-two\"\n")
    let outcome = nanyxIn directory.Path [ "check"; "main.nyx" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("main.nyx:1:19: error NYX0201: Type mismatch: 'string' vs 'int'", outcome.StandardOutput)

[<Fact>]
let ``Exit codes - a script that checks exits with 0`` () =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), "def answer: int = 42\n")
    let outcome = nanyxIn directory.Path [ "check"; "main.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
