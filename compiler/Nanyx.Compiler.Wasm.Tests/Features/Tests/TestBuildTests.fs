/// Tests and test builds (#2930): a def marked `@test`, beside the code or in a file the manifest's `tests` selects, is
/// part of a test build alone, which exports each test and lists it with its name and place; another build leaves
/// tests and test files out, and only a test may refer to a test.
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

/// The test build of the package in `directory`: its module, in the binary format, and the tests it lists.
let private testBuild (directory: TempDirectory) =
    match Package.ofManifestFrom (testStore ()) (Path.Combine(directory.Path, "geometry.nyxp")) with
    | Ok found ->
        let build = Package.testBuild found
        Assert.Empty(build.Diagnostics |> List.filter Diagnostics.isError)
        let root = (Package.testResult build).Value
        Assert.Empty(root.Diagnostics |> List.filter Diagnostics.isError)
        let options = { Transpiler.Wasm.CodeGen.WasmOptions.Default with Places = true; SourceRoot = Some directory.Path }
        let wasm = Transpiler.Wasm.TestBuild.assemble options root.Typed.Value
        wasm, Nanyx.Runtime.TestTable.read wasm
    | Error problems -> failwith $"%A{problems}"

[<Fact>]
let ``A test build lists each test, beside the code and in test files, with its name and place`` () =
    use directory = package "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let _, tests = testBuild directory
    let listed = tests |> List.map (fun test -> test.FullName, $"{test.File}:{test.Line}:{test.Column}") |> List.sort
    Assert.Equal<(string * string) list>(
        [ "geometry.doubles", "src/geometry.nyx:8:1"
          "geometry.tests.adds up", "tests/geometry_tests.nyx:6:1"
          "geometry.tests.fails", "tests/geometry_tests.nyx:11:1" ],
        listed)

[<Fact>]
let ``Calling a test's export runs it: one that holds returns, and one whose assert fails crashes`` () =
    use directory = package "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let wasm, tests = testBuild directory
    let outcome (name: string) =
        let index = tests |> List.findIndex (fun test -> test.FullName = name)
        use engine = new Wasmtime.Engine()
        use store = new Wasmtime.Store(engine)
        use compiled = Wasmtime.Module.FromBytes(engine, "tests", wasm)
        use linker = new Wasmtime.Linker(engine)
        // A failing assert writes its message to stderr before it traps.
        linker.DefineWasi()
        store.SetWasiConfiguration(Wasmtime.WasiConfiguration())
        let instance = linker.Instantiate(store, compiled)
        try
            instance.GetFunction<int>(Nanyx.Runtime.TestTable.exportName index).Invoke() |> ignore
            "passed"
        with :? Wasmtime.WasmtimeException -> "crashed"
    Assert.Equal("passed", outcome "geometry.doubles")
    Assert.Equal("passed", outcome "geometry.tests.adds up")
    Assert.Equal("crashed", outcome "geometry.tests.fails")

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
