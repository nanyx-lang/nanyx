/// `nanyx test --format json` (#3079): one JSON object on each line of standard output, each with a `type`, for a tool
/// such as an editor to read.
module TranspilerWasmTests.Features.Tests.TestJsonFormat

open System.Text.Json
open Xunit
open TranspilerWasmTestHelpers

let private geometry =
    "export module geometry\n\nexport def double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles a number` = {\n  assert(double(2) == 4)\n}\n\n@test\ndef halves = {\n  print(\"halving\")\n  assert(double(3) == 5)\n}\n"

/// What `nanyx test --format json`, with `options`, wrote for the package whose files are `files`: its exit code and
/// each line it wrote, read as JSON.
let private jsonOf (options: string list) (files: (string * string) list) =
    use directory = testedPackage "" files
    let outcome = nanyxIn directory.Path ([ "test"; "--format"; "json" ] @ options)
    let objects =
        outcome.StandardOutput.ReplaceLineEndings("\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> JsonDocument.Parse(line).RootElement.Clone())
        |> List.ofArray
    outcome.ExitCode, objects

let private text (element: JsonElement) (name: string) = element.GetProperty(name).GetString()
let private number (element: JsonElement) (name: string) = element.GetProperty(name).GetInt64()
let private types (objects: JsonElement list) = objects |> List.map (fun element -> text element "type")

[<Fact>]
let ``It writes a start, each test as it ends, and a summary`` () =
    let exitCode, objects = jsonOf [ "--seed"; "7" ] [ "src/geometry.nyx", geometry ]
    Assert.Equal(1, exitCode)
    Assert.Equal<string list>([ "start"; "test"; "test"; "summary" ], types objects)
    let start = objects.[0]
    Assert.Equal(("tested", 2L, 7L, 0L), (text start "name", number start "tests", number start "seed", number start "deferredErrors"))
    let doubles = objects |> List.find (fun element -> text element "type" = "test" && text element "test" = "doubles a number")
    Assert.Equal("geometry.`doubles a number`", text doubles "name")
    Assert.Equal(("geometry", "src/geometry.nyx", 6L, 1L), (text doubles "module", text doubles "file", number doubles "line", number doubles "column"))
    Assert.Equal("passed", text doubles "outcome")
    Assert.False(doubles.GetProperty("io").GetBoolean())
    let summary = List.last objects
    Assert.Equal((true, 1L, 1L, 7L), (summary.GetProperty("built").GetBoolean(), number summary "passed", number summary "failed", number summary "seed"))

[<Fact>]
let ``A test that fails has why: the crash's message, its place and its stack, and what it printed`` () =
    let _, objects = jsonOf [] [ "src/geometry.nyx", geometry ]
    let halves = objects |> List.find (fun element -> text element "type" = "test" && text element "test" = "halves")
    Assert.Equal(("failed", "halving"), (text halves "outcome", text halves "output"))
    let failure = halves.GetProperty "failure"
    Assert.Equal("crashed", text failure "kind")
    Assert.StartsWith("src/geometry.nyx:13:3: assert(double(3) == 5)\n  double(3) = 6\n", text failure "message")
    let place = failure.GetProperty "place"
    Assert.Equal(("src/geometry.nyx", 13L, 3L), (text place "file", number place "line", number place "column"))
    let frame = failure.GetProperty("stack").[0]
    Assert.Equal(("geometry_halves", "src/geometry.nyx", 13L), (text frame "function", text frame "file", number frame "line"))
    Assert.Equal(text failure "message", text halves "errors")

[<Fact>]
let ``A test that runs past its time is timedOut`` () =
    let spinning = "export module spinning\n\ndef spin: int -> int = { n -> spin(n + 1) }\n\n@test\ndef `never ends` = {\n  assert(spin(0) == 0)\n}\n"
    let _, objects = jsonOf [ "--timeout"; "0.2" ] [ "src/spinning.nyx", spinning ]
    let test = objects |> List.find (fun element -> text element "type" = "test")
    Assert.Equal("timedOut", text test "outcome")
    Assert.Equal(0.2, test.GetProperty("timeoutSeconds").GetDouble(), 3)

[<Fact>]
let ``A test marked #io says so`` () =
    let io = "export module io\n\n@test(#io)\ndef `prints` = {\n  println(\"to the real console\")\n}\n"
    let exitCode, objects = jsonOf [] [ "src/io.nyx", io ]
    Assert.Equal(0, exitCode)
    let test = objects |> List.find (fun element -> text element "type" = "test")
    Assert.True(test.GetProperty("io").GetBoolean())
    Assert.Equal("passed", text test "outcome")

[<Fact>]
let ``A build that fails writes its diagnostics and a summary that says the tests weren't built`` () =
    let broken = "export module broken\n\n@test\ndef `adds` = {\n  assert(1 + == 2)\n}\n"
    let exitCode, objects = jsonOf [] [ "src/broken.nyx", broken; "src/geometry.nyx", geometry ]
    Assert.Equal(1, exitCode)
    Assert.DoesNotContain("start", types objects)
    let diagnostic = objects |> List.find (fun element -> text element "type" = "diagnostic")
    Assert.Equal(("NYX0001", "error", "src/broken.nyx", 5L), (text diagnostic "code", text diagnostic "severity", text diagnostic "file", number diagnostic "line"))
    Assert.False(diagnostic.GetProperty("deferred").GetBoolean())
    let summary = List.last objects
    Assert.Equal(("summary", false), (text summary "type", summary.GetProperty("built").GetBoolean()))

[<Fact>]
let ``A package with no tests writes only a summary`` () =
    let exitCode, objects = jsonOf [] [ "src/plain.nyx", "export module plain\n\nexport def one: int = 1\n" ]
    Assert.Equal(0, exitCode)
    Assert.Equal<string list>([ "summary" ], types objects)
    Assert.Equal(0L, number objects.[0] "passed")

[<Fact>]
let ``A --format that is neither text nor json is refused`` () =
    use directory = testedPackage "" [ "src/geometry.nyx", geometry ]
    let outcome = nanyxIn directory.Path [ "test"; "--format"; "xml" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("--format is text or json, not 'xml'", outcome.StandardError)

[<Fact>]
let ``A test that expects a crash says so, and is passed, didNotCrash or crashedOtherwise`` () =
    let checks =
        "export module checks\n\n@test(#crashes)\ndef `crashes` = {\n  crash(\"boom\")\n}\n\n@test(#crashes(\"too big\"))\ndef `crashes otherwise` = {\n  crash(\"boom\")\n}\n\n@test(#crashes)\ndef `finishes` = {\n  println(\"done\")\n}\n\n@test\ndef `plain` = {\n  println(\"done\")\n}\n"
    let _, objects = jsonOf [] [ "src/checks.nyx", checks ]
    let test (name: string) = objects |> List.find (fun element -> text element "type" = "test" && text element "test" = name)
    Assert.Equal("passed", text (test "crashes") "outcome")
    Assert.Equal(0, (test "crashes").GetProperty("crashes").EnumerateObject() |> Seq.length)
    let otherwise = test "crashes otherwise"
    Assert.Equal(("crashedOtherwise", "too big", "too big"), (text otherwise "outcome", text otherwise "expected", text (otherwise.GetProperty "crashes") "text"))
    Assert.Equal("boom", text (otherwise.GetProperty "failure") "message")
    Assert.Equal("didNotCrash", text (test "finishes") "outcome")
    Assert.Equal(JsonValueKind.Null, (test "plain").GetProperty("crashes").ValueKind)

[<Fact>]
let ``A test lists each dbg it reached, in order, with its place, and its output keeps them too (#2054)`` () =
    let source = "export module counting\n\n@test\ndef `counts` = {\n  for [1, 2] | n -> dbg(n * 10)\n  assert(true)\n}\n"
    let _, objects = jsonOf [] [ "src/counting.nyx", source ]
    let counts = objects |> List.find (fun element -> text element "type" = "test")
    let hits =
        counts.GetProperty("dbg").EnumerateArray()
        |> Seq.map (fun hit -> text hit "file", number hit "line", number hit "column", text hit "value")
        |> List.ofSeq
    Assert.Equal<(string * int64 * int64 * string) list>([ "src/counting.nyx", 5L, 21L, "10"; "src/counting.nyx", 5L, 21L, "20" ], hits)
    Assert.Equal("[src/counting.nyx:5] 10\n[src/counting.nyx:5] 20\n", text counts "output")
