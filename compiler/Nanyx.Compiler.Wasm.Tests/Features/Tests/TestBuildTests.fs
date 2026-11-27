/// Tests and test builds (#2930, #2931): a def marked `@test`, beside the code or in a file the manifest's `tests`
/// selects, is part of a test build alone, which runs under the test host, lists each test with its name and place, and
/// runs the one at the index the host gives it; another build leaves tests and test files out.
module TranspilerWasmTests.Features.Tests.TestBuilds

open System
open System.IO
open Xunit
open NyxCompiler
open Nanyx.Runtime
open TranspilerWasmTestHelpers

let private geometry =
    "export module geometry\n\nexport def add: int, int -> int = { a, b -> a + b }\n\ndef double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles` = {\n  assert(double(2) == 4)\n}\n"

let private geometryTests =
    "module geometry.tests\n\nimport geometry as (add)\n\n@test\ndef `adds up` = {\n  assert(add(1, 2) == 3)\n}\n\n@test\ndef `fails` = {\n  assert(add(1, 2) == 4)\n}\n"

/// The test build of `files`, with no errors, and the tests it lists.
let private testBuild (tests: string) (files: (string * string) list) =
    use directory = testedPackage tests files
    let wasm, errors = testBuildOf directory
    Assert.Empty(errors)
    wasm, TestTable.read wasm

[<Fact>]
let ``A test build lists each test, beside the code and in test files, with its name and place`` () =
    let _, tests = testBuild "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let listed = tests |> List.map (fun test -> test.FullName, $"{test.File}:{test.Line}:{test.Column}")
    Assert.Equal<(string * string) list>(
        [ "geometry.doubles", "src/geometry.nyx:8:1"
          "geometry.tests.adds up", "tests/geometry_tests.nyx:6:1"
          "geometry.tests.fails", "tests/geometry_tests.nyx:11:1" ],
        listed)

[<Fact>]
let ``The test host runs the test at the index it is given: one that holds passes, and one whose assert fails crashes`` () =
    let wasm, _ = testBuild "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let runs = runTestsOf wasm 0L WasmTester.defaultTimeout
    Assert.Equal(Passed, runs.["geometry.doubles"].Outcome)
    Assert.Equal(Passed, runs.["geometry.tests.adds up"].Outcome)
    match runs.["geometry.tests.fails"].Outcome with
    | Failed(Crashed(_, Some(file, line, column), _)) -> Assert.Equal(("tests/geometry_tests.nyx", 12, 3), (file, line, column))
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A test's output goes to the runtime, and its Random is seeded with the runtime's seed`` () =
    let rolls =
        "module rolls\n\nimport nanyx/random as random\n\n@test\ndef `rolls` = {\n  println(\"{random.below(1_000_000)} {random.below(1_000_000)}\")\n}\n\n@test\ndef `says hello` = {\n  print(\"hello\")\n}\n"
    let wasm, _ = testBuild "" [ "src/geometry.nyx", geometry; "src/rolls.nyx", rolls ]
    let output seed = runTestsOf wasm seed WasmTester.defaultTimeout |> Map.map (fun _ run -> run.Outcome, run.Output)
    Assert.Equal((Passed, "hello"), (output 0L).["rolls.says hello"])
    let first = (output 42L).["rolls.rolls"]
    Assert.Equal(Passed, fst first)
    Assert.Equal(first, (output 42L).["rolls.rolls"])
    Assert.NotEqual(first, (output 43L).["rolls.rolls"])

[<Fact>]
let ``A test build runs the tests, not the program's entry point`` () =
    let app = "module app\n\n@entry\ndef main = {\n  println(\"the program\")\n}\n\n@test\ndef `runs` = {\n  print(\"the test\")\n}\n"
    let wasm, _ = testBuild "" [ "src/geometry.nyx", geometry; "src/app.nyx", app ]
    let run = (runTestsOf wasm 0L WasmTester.defaultTimeout).["app.runs"]
    Assert.Equal((Passed, "the test"), (run.Outcome, run.Output))

[<Fact>]
let ``A build that isn't a test build leaves test files out`` () =
    use directory = testedPackage "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    match Package.ofManifestFrom (testStore ()) (Path.Combine(directory.Path, "tested.nyxp")) with
    | Ok found ->
        Assert.Equal<string list>([ "geometry.nyx" ], found.Sources |> List.map Path.GetFileName)
        let build = Package.build found
        Assert.Empty(build.Diagnostics |> List.filter Diagnostics.isError)
    | Error problems -> failwith $"%A{problems}"

[<Fact>]
let ``A file both sources and tests select is an error in the manifest`` () =
    use directory = testedPackage "src/**/*.nyx" [ "src/geometry.nyx", geometry ]
    match Package.ofManifestFrom (testStore ()) (Path.Combine(directory.Path, "tested.nyxp")) with
    | Ok _ -> Assert.Fail "no error"
    | Error problems -> Assert.Contains(problems, fun problem -> problem.Contains "with both `sources` and `tests`")
