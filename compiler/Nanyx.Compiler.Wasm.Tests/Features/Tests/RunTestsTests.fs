/// Running a test build's tests (#2931): each in an instance of its own, so that one that fails, however it fails,
/// fails alone (`Nanyx.Runtime.WasmTester`).
module TranspilerWasmTests.Features.Tests.RunTests

open System
open Xunit
open NyxCompiler
open Nanyx.Runtime
open TranspilerWasmTestHelpers

/// Each test of a package whose one module is `source`, by its name in the module, run with a time limit of `timeout`;
/// and the errors its build deferred to them.
let private runsOf (timeout: TimeSpan) (source: string) =
    use directory = testedPackage "" [ "src/checks.nyx", "export module checks\n\nexport def answer: int = 42\n\n" + source ]
    let wasm, errors = testBuildOf directory
    runTestsOf wasm 0L timeout |> Map.toList |> List.map (fun (name, run) -> name.Substring("checks.".Length), run) |> Map.ofList, errors

[<Fact>]
let ``A test that crashes fails with the crash's message and place, and the tests after it still run`` () =
    let runs, _ =
        runsOf WasmTester.defaultTimeout "@test\ndef `crashes` = {\n  crash(\"boom\")\n}\n\n@test\ndef `passes` = {\n  assert(answer == 42)\n}\n"
    match runs.["crashes"].Outcome with
    | Failed(Crashed(Some message, Some(file, line, column), _)) ->
        Assert.Contains("boom", message)
        Assert.Equal(("src/checks.nyx", 7, 3), (file, line, column))
    | other -> Assert.Fail $"%A{other}"
    Assert.Equal(Passed, runs.["passes"].Outcome)

[<Fact>]
let ``A test that runs past its time limit is stopped, and fails`` () =
    let timeout = TimeSpan.FromMilliseconds 200.0
    let runs, _ =
        runsOf timeout "def spin: int -> int = { n -> spin(n + 1) }\n\n@test\ndef `never ends` = {\n  assert(spin(0) == 0)\n}\n\n@test\ndef `passes` = {\n  assert(answer == 42)\n}\n"
    Assert.Equal(TimedOut timeout, runs.["never ends"].Outcome)
    Assert.Equal(Passed, runs.["passes"].Outcome)

[<Fact>]
let ``A test's output and its dbgs are kept apart from the others'`` () =
    let runs, _ =
        runsOf WasmTester.defaultTimeout "@test\ndef `prints` = {\n  print(\"one\")\n  ignore(dbg(answer))\n}\n\n@test\ndef `prints too` = {\n  print(\"two\")\n}\n"
    Assert.Equal("one[src/checks.nyx:8] 42\n", runs.["prints"].Output)
    Assert.Equal("two", runs.["prints too"].Output)

[<Fact>]
let ``The runner runs only the tests it is asked to, in the order of their indexes`` () =
    use directory = testedPackage "" [ "src/checks.nyx", "export module checks\n\nexport def answer: int = 42\n\n@test\ndef `one` = {\n  print(\"1\")\n}\n\n@test\ndef `two` = {\n  print(\"2\")\n}\n\n@test\ndef `three` = {\n  print(\"3\")\n}\n" ]
    let wasm, _ = testBuildOf directory
    let reported = ResizeArray<string>()
    match WasmTester.run wasm directory.Path [] (fun test -> test.Name <> "two") 0L WasmTester.defaultTimeout (fun run -> reported.Add run.Output) with
    | Ok runs ->
        Assert.Equal<int list>([ 0; 2 ], runs |> List.map _.Index)
        Assert.Equal<string list>([ "1"; "3" ], List.ofSeq reported)
    | Error error -> Assert.Fail $"%A{error}"

[<Fact>]
let ``A deferred error in a def of the test's own module keeps the def's frame, at the error`` () =
    // The root imports the module as `__test0`, which the def's name starts with in the build (#3081).
    let runs, _ =
        runsOf WasmTester.defaultTimeout "export def perimeter: int -> int = { n -> n * \"4\" }\n\n@test\ndef `perimeters` = {\n  assert(perimeter(3) == 12)\n}\n"
    match runs.["perimeters"].Outcome with
    | Failed(Crashed(Some message, place, stack)) ->
        Assert.StartsWith("src/checks.nyx:5:43: error NYX0201", message)
        Assert.Equal(Some("src/checks.nyx", 5, 43), place)
        Assert.Equal<string list>([ "perimeter (src/checks.nyx:5)"; "perimeters (src/checks.nyx:9)" ], stack |> List.map string)
    | other -> Assert.Fail $"%A{other}"
