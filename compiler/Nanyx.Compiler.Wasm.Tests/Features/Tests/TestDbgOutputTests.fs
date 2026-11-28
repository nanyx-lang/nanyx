/// What each test's `dbg`s print is kept with the test that printed it (#2103): a failing test's report shows it under
/// its failure, a passing test's is dropped unless `--show-output` asks for it, and `--format json` carries each test's
/// hits whatever its outcome.
module TranspilerWasmTests.Features.Tests.TestDbgOutput

open System.Text.Json
open Xunit
open TranspilerWasmTestHelpers

let private checks =
    "def double = { n: int -> n * 2 }\n\n@test\ndef passes = {\n  dbg(double(2))\n  assert(double(2) == 4)\n}\n\n@test\ndef fails = {\n  dbg(double(3))\n  assert(double(3) == 5)\n}\n"

[<Fact>]
let ``A failing test's dbg is shown under its failure, and a passing test's is dropped`` () =
    use directory = new TempDirectory()
    directory.Write "checks.nyx" checks
    let outcome = nanyxIn directory.Path [ "test"; "checks.nyx" ]
    let output = outcome.StandardOutput.ReplaceLineEndings "\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("PASS checks.passes\nFAIL checks.fails", output)
    Assert.Contains("  Output:\n    [checks.nyx:11] 6\n", output)
    Assert.DoesNotContain("[checks.nyx:5]", output)

[<Fact>]
let ``With --show-output, a passing test's dbg is shown under it too`` () =
    use directory = new TempDirectory()
    directory.Write "checks.nyx" checks
    let output = (nanyxIn directory.Path [ "test"; "--show-output"; "checks.nyx" ]).StandardOutput.ReplaceLineEndings "\n"
    Assert.Contains("PASS checks.passes\n  Output:\n    [checks.nyx:5] 4\n", output)
    Assert.Contains("  Output:\n    [checks.nyx:11] 6\n", output)

[<Fact>]
let ``--format json carries each test's dbg hits, a passing test's among them`` () =
    use directory = new TempDirectory()
    directory.Write "checks.nyx" checks
    let lines = (nanyxIn directory.Path [ "test"; "--format"; "json"; "checks.nyx" ]).StandardOutput.Split('\n') |> Array.filter (fun line -> line.Contains "\"type\":\"test\"")
    let hits =
        lines
        |> Array.map (fun line ->
            let json = JsonDocument.Parse(line).RootElement
            json.GetProperty("test").GetString(),
            [ for hit in json.GetProperty("dbg").EnumerateArray() -> hit.GetProperty("line").GetInt32(), hit.GetProperty("value").GetString() ])
        |> List.ofArray
    Assert.Equal<(string * (int * string) list) list>([ "passes", [ 5, "4" ]; "fails", [ 11, "6" ] ], hits)
