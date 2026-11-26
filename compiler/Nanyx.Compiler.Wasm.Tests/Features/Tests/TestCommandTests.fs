/// `nanyx test` (#2931): builds a package's tests under the test host, runs each in an instance of its own, and reports
/// each, with why one failed and what it printed, and the seed to run them again with.
module TranspilerWasmTests.Features.Tests.TestCommand

open Xunit
open TranspilerWasmTestHelpers

let private geometry =
    "export module geometry\n\nexport def double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles a number` = {\n  assert(double(2) == 4)\n}\n\n@test\ndef halves = {\n  print(\"halving\")\n  assert(double(3) == 5)\n}\n"

/// What `nanyx test` printed, with `options`, for the package whose files are `files`.
let private testing (options: string list) (files: (string * string) list) =
    use directory = testedPackage "" files
    nanyxIn directory.Path ([ "test" ] @ options)

[<Fact>]
let ``nanyx test reports each test, with why one failed and what it printed, and ends with 1`` () =
    let outcome = testing [ "--seed"; "7" ] [ "src/geometry.nyx", geometry ]
    Assert.Equal(1, outcome.ExitCode)
    let expected =
        [ "Running 2 tests in tested"
          ""
          "PASS geometry.`doubles a number`"
          "FAIL geometry.halves  src/geometry.nyx:11:1"
          "  Output:"
          "    halving"
          ""
          "1 passed, 1 failed"
          "Seed 7: run them again with the same random numbers with --seed 7"
          "" ]
    let lines = outcome.StandardOutput.ReplaceLineEndings("\n").Split('\n') |> List.ofArray
    // The assert's message and the stack come between the FAIL line and the output.
    let withoutWhy = lines |> List.filter (fun line -> not (line.StartsWith "  ") || line.StartsWith "  Output:" || line.StartsWith "    halving")
    Assert.Equal<string list>(expected, withoutWhy)
    Assert.Contains("assert(double(3) == 5)\n    double(3) = 6\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx test ends with 0 when every test passes, and shows no output of one that passes`` () =
    let outcome = testing [ "--filter"; "doubles" ] [ "src/geometry.nyx", geometry ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("Running 1 test in tested\n\nPASS geometry.`doubles a number`\n\n1 passed\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx test stops a test that runs past --timeout`` () =
    let spinning = "export module spinning\n\ndef spin: int -> int = { n -> spin(n + 1) }\n\n@test\ndef `never ends` = {\n  assert(spin(0) == 0)\n}\n"
    let outcome = testing [ "--timeout"; "0.2" ] [ "src/spinning.nyx", spinning ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("It ran for longer than 0.2 seconds, and was stopped: give it longer with --timeout", outcome.StandardOutput)

[<Fact>]
let ``nanyx test reports a build error and runs nothing`` () =
    let broken = "export module broken\n\n@test\ndef `adds` = {\n  assert(1 + \"1\" == 2)\n}\n"
    let outcome = testing [] [ "src/broken.nyx", broken; "src/geometry.nyx", geometry ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("NYX0201", outcome.StandardError)
    Assert.DoesNotContain("Running", outcome.StandardOutput)

[<Fact>]
let ``nanyx test says so when a package has no tests`` () =
    let outcome = testing [] [ "src/plain.nyx", "export module plain\n\nexport def one: int = 1\n" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("tested has no tests: mark a def @test to make it one\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx test refuses a seed that isn't a whole number`` () =
    let outcome = testing [ "--seed"; "seven" ] [ "src/geometry.nyx", geometry ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("--seed is a whole number", outcome.StandardError)

[<Fact>]
let ``nanyx test runs a script's tests, not its statements`` () =
    use directory = new TempDirectory()
    directory.Write "maths.nyx" "def double: int -> int = { n -> n * 2 }\n\nprintln(\"the script runs\")\n\n@test\ndef `doubles` = {\n  print(\"doubling\")\n  assert(double(2) == 4)\n}\n"
    let outcome = nanyxIn directory.Path [ "test"; "maths.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("Running 1 test in maths.nyx\n\nPASS maths.doubles\n\n1 passed\n", outcome.StandardOutput.ReplaceLineEndings "\n")
