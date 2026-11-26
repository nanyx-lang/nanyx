/// Tests and test builds (#2930, #2931): a def marked `@test`, beside the code or in a file the manifest's `tests`
/// selects, is part of a test build alone, which runs under the test host, lists each test with its name and place, and
/// runs the one at the index the host gives it; another build leaves tests and test files out.
module TranspilerWasmTests.Features.Tests.TestBuilds

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// The package `geometry`, with `files` as its files, by their paths relative to its directory, and `tests` its
/// manifest's `tests`, written to a new directory.
let private package (tests: string) (files: (string * string) list) =
    let directory = new TempDirectory()
    let testsField = if tests = "" then "" else $", tests = [\"{tests}\"]"
    File.WriteAllText(Path.Combine(directory.Path, "geometry.nyxp"), $"""(package = (name = "geometry", version = "0.1.0", sources = ["src/**/*.nyx"]{testsField}))""" + "\n")
    for path, text in files do
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text)
    directory

let private geometry =
    "export module geometry\n\nexport def add: int, int -> int = { a, b -> a + b }\n\ndef double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles` = {\n  assert(double(2) == 4)\n}\n"

let private geometryTests =
    "module geometry.tests\n\nimport geometry as (add)\n\n@test\ndef `adds up` = {\n  assert(add(1, 2) == 3)\n}\n\n@test\ndef `fails` = {\n  assert(add(1, 2) == 4)\n}\n"

/// The test build of the package in `directory`, under the test host: its module, in the binary format, and the tests
/// it lists.
let private testBuild (directory: TempDirectory) =
    let store = testStore ()
    match Package.ofManifestFrom store (Path.Combine(directory.Path, "geometry.nyxp")) with
    | Ok found ->
        let build = (Package.testBuild store found) |> Result.defaultWith failwith
        Assert.Empty(build.Diagnostics |> List.filter Diagnostics.isError)
        let root = (Package.testResult build).Value
        Assert.Empty(root.Diagnostics |> List.filter Diagnostics.isError)
        let options = { Transpiler.Wasm.CodeGen.WasmOptions.Default with Places = true; SourceRoot = Some directory.Path }
        let wasm = Transpiler.Wasm.TestBuild.assemble options root.Typed.Value
        wasm, Nanyx.Runtime.TestTable.read wasm
    | Error problems -> failwith $"%A{problems}"

/// What running the test at `index` of the test build `wasm` with `seed` did, as the runtime runs it: in an instance of
/// its own, whose `nanyx:test` gives the host the index and the seed, and takes what the test writes. Whether it passed,
/// and what it wrote.
let private run (wasm: byte array) (index: int) (seed: int64) =
    use engine = new Wasmtime.Engine()
    use store = new Wasmtime.Store(engine)
    use compiled = Wasmtime.Module.FromBytes(engine, "tests", wasm)
    use linker = new Wasmtime.Linker(engine)
    // A failing assert writes its message to stderr before it traps.
    linker.DefineWasi()
    store.SetWasiConfiguration(Wasmtime.WasiConfiguration())
    let written = System.Text.StringBuilder()
    for import in compiled.Imports do
        match import with
        | :? Wasmtime.FunctionImport as func when func.ModuleName = "nanyx:test" ->
            let callback =
                match func.Name with
                | "index" -> Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit index)
                | "seed" -> Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit seed)
                | _ ->
                    Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments _ ->
                        let memory = caller.GetMemory("memory")
                        written.Append(memory.ReadString(int64 (arguments.[0].AsInt32()), arguments.[1].AsInt32())) |> ignore)
            linker.Define(func.ModuleName, func.Name, Wasmtime.Function.FromCallback(store, callback, func.Parameters, func.Results))
        | _ -> ()
    // The test runs in the module's start function, as the program does.
    let passed =
        try
            linker.Instantiate(store, compiled) |> ignore
            true
        with :? Wasmtime.WasmtimeException -> false
    passed, written.ToString()

[<Fact>]
let ``A test build lists each test, beside the code and in test files, with its name and place`` () =
    use directory = package "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let _, tests = testBuild directory
    let listed = tests |> List.map (fun test -> test.FullName, $"{test.File}:{test.Line}:{test.Column}")
    Assert.Equal<(string * string) list>(
        [ "geometry.doubles", "src/geometry.nyx:8:1"
          "geometry.tests.adds up", "tests/geometry_tests.nyx:6:1"
          "geometry.tests.fails", "tests/geometry_tests.nyx:11:1" ],
        listed)

[<Fact>]
let ``The test host runs the test at the index it is given: one that holds finishes, and one whose assert fails crashes`` () =
    use directory = package "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let wasm, tests = testBuild directory
    let outcome (name: string) = fst (run wasm (tests |> List.findIndex (fun test -> test.FullName = name)) 0L)
    Assert.True(outcome "geometry.doubles")
    Assert.True(outcome "geometry.tests.adds up")
    Assert.False(outcome "geometry.tests.fails")

[<Fact>]
let ``A test's output goes to the runtime, and its Random is seeded with the runtime's seed`` () =
    let rolls =
        "module rolls\n\nimport nanyx/random as random\n\n@test\ndef `rolls` = {\n  println(\"{random.below(1_000_000)} {random.below(1_000_000)}\")\n}\n\n@test\ndef `says hello` = {\n  print(\"hello\")\n}\n"
    use directory = package "" [ "src/geometry.nyx", geometry; "src/rolls.nyx", rolls ]
    let wasm, tests = testBuild directory
    let index name = tests |> List.findIndex (fun test -> test.FullName = name)
    Assert.Equal((true, "hello"), run wasm (index "rolls.says hello") 0L)
    let first = run wasm (index "rolls.rolls") 42L
    Assert.True(fst first)
    Assert.Equal(first, run wasm (index "rolls.rolls") 42L)
    Assert.NotEqual(first, run wasm (index "rolls.rolls") 43L)

[<Fact>]
let ``A test build runs the tests, not the program's entry point`` () =
    let app = "module app\n\n@entry\ndef main = {\n  println(\"the program\")\n}\n\n@test\ndef `runs` = {\n  print(\"the test\")\n}\n"
    use directory = package "" [ "src/geometry.nyx", geometry; "src/app.nyx", app ]
    let wasm, tests = testBuild directory
    Assert.Equal((true, "the test"), run wasm (tests |> List.findIndex (fun test -> test.FullName = "app.runs")) 0L)

[<Fact>]
let ``A build that isn't a test build leaves test files out, and tests beside the code unreached`` () =
    use directory = package "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    match Package.ofManifestFrom (testStore ()) (Path.Combine(directory.Path, "geometry.nyxp")) with
    | Ok found ->
        Assert.Equal<string list>([ "geometry.nyx" ], found.Sources |> List.map Path.GetFileName)
        let build = Package.build found
        Assert.Empty(build.Diagnostics |> List.filter Diagnostics.isError)
    | Error problems -> failwith $"%A{problems}"

[<Fact>]
let ``A file both sources and tests select is an error in the manifest`` () =
    use directory = package "src/**/*.nyx" [ "src/geometry.nyx", geometry ]
    match Package.ofManifestFrom (testStore ()) (Path.Combine(directory.Path, "geometry.nyxp")) with
    | Ok _ -> Assert.Fail "no error"
    | Error problems -> Assert.Contains(problems, fun problem -> problem.Contains "with both `sources` and `tests`")
