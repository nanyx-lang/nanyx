/// `@test(#crashes)` (#3078): a test that passes only if it crashes, as `crash`, `orCrash` and `assert` do, and with
/// `#crashes("text")`, only with a message that contains the text. A deferred error, a runtime error and a timeout fail it
/// as they fail any test.
module TranspilerWasmTests.Features.Tests.ExpectedCrashes

open Xunit
open TranspilerWasmTestHelpers

/// `first`, which crashes on an empty list.
let private first =
    "export module checks\n\ndef first: list(int) -> int = { xs ->\n  match xs\n    | [x, ..._] -> x\n    | [] -> crash(\"no first element\")\n}\n\n"

/// What `nanyx test --seed 1` printed for a package whose one module is `first` and then `tests`: its exit code, and
/// its standard output's lines.
let private testing (tests: string) =
    use directory = testedPackage "" [ "src/checks.nyx", first + tests ]
    let outcome = nanyxIn directory.Path [ "test"; "--seed"; "1" ]
    outcome.ExitCode, outcome.StandardOutput.ReplaceLineEndings "\n"

[<Fact>]
let ``A test marked @test(#crashes) passes when it crashes, as crash, orCrash and assert do`` () =
    let exitCode, output =
        testing
            "@test(#crashes)\ndef `crashes` = {\n  println(first([]))\n}\n\n@test(#crashes)\ndef `or crashes` = {\n  def found: #some(int) | #none = #none\n  println(found \\orCrash(\"none\"))\n}\n\n@test(#crashes)\ndef `asserts` = {\n  assert(first([1]) == 2)\n}\n"
    Assert.Equal(0, exitCode)
    Assert.Contains("PASS checks.crashes (crashes)\n", output)
    Assert.Contains("PASS checks.`or crashes` (crashes)\n", output)
    Assert.Contains("PASS checks.asserts (crashes)\n", output)
    Assert.Contains("3 passed\n", output)

[<Fact>]
let ``A test marked @test(#crashes) that finishes fails, saying it was expected to crash`` () =
    let exitCode, output = testing "@test(#crashes)\ndef `finishes` = {\n  println(first([1]))\n}\n"
    Assert.Equal(1, exitCode)
    Assert.Contains("FAIL checks.finishes  src/checks.nyx:10:1\n  It finished, and was expected to crash\n  Output:\n    1\n", output)

[<Fact>]
let ``A test marked @test(#crashes("text")) passes only when the crash's message contains the text`` () =
    let exitCode, output =
        testing
            "@test(#crashes(\"no first\"))\ndef `says why` = {\n  println(first([]))\n}\n\n@test(#crashes(\"too big\"))\ndef `says something else` = {\n  println(first([]))\n}\n"
    Assert.Equal(1, exitCode)
    Assert.Contains("PASS checks.`says why` (crashes)\n", output)
    Assert.Contains(
        "FAIL checks.`says something else`  src/checks.nyx:15:1\n  It was expected to crash with a message that contains \"too big\", and crashed with another:\n  src/checks.nyx:6:13: crash: no first element\n",
        output
    )

[<Fact>]
let ``A test marked @test(#io, #crashes) runs with the real contexts and expects a crash`` () =
    let exitCode, output = testing "@test(#io, #crashes)\ndef `io and crashes` = {\n  crash(\"boom\")\n}\n"
    Assert.Equal(0, exitCode)
    Assert.Contains("PASS checks.`io and crashes` (io) (crashes)\n", output)

[<Fact>]
let ``A deferred error, an unhandled case and a runtime error fail a test that expects a crash`` () =
    // An error the compiler found isn't a crash a test can expect, a match that doesn't handle every case among them,
    // and neither is a runtime error such as an overflow.
    let exitCode, output =
        testing
            "def broken: int -> int = { n -> n * \"4\" }\n\n@test(#crashes)\ndef `reaches a type error` = {\n  println(broken(1))\n}\n\ndef name: int -> string = { n ->\n  match n\n    | 1 -> \"one\"\n}\n\n@test(#crashes)\ndef `reaches an unhandled case` = {\n  println(name(2))\n}\n\n@test(#crashes)\ndef `overflows` = {\n  def big = 9223372036854775807\n  println(big + first([1]))\n}\n"
    Assert.Equal(1, exitCode)
    Assert.Contains("FAIL checks.`reaches a type error`", output)
    Assert.Contains("error NYX0201: Type mismatch", output)
    Assert.Contains("FAIL checks.`reaches an unhandled case`", output)
    Assert.Contains("error NYX0203: Non-exhaustive match", output)
    Assert.Contains("FAIL checks.overflows", output)
    Assert.Contains("Runtime error: integer overflow", output)
    Assert.Contains("0 passed, 3 failed\n", output)

[<Fact>]
let ``A test marked @test(#crashes) that runs past its time fails`` () =
    use directory = testedPackage "" [ "src/checks.nyx", first + "def spin: int -> int = { n -> spin(n + 1) }\n\n@test(#crashes)\ndef `never ends` = {\n  println(spin(0))\n}\n" ]
    let outcome = nanyxIn directory.Path [ "test"; "--timeout"; "0.2" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("It ran for longer than 0.2 seconds, and was stopped", outcome.StandardOutput)
