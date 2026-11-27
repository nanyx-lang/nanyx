/// `nanyx test --list`, which lists the tests a run would run and runs none, and `--test` and `--module`, which run
/// exactly the tests they name (#3099).
module TranspilerWasmTests.Features.Tests.TestSelection

open System.Text.Json
open Xunit
open TranspilerWasmTestHelpers

let private geometry =
    "export module geometry\n\nexport def double: int -> int = { n -> n * 2 }\n\n@test\ndef `adds` = {\n  assert(double(2) == 4)\n}\n\n@test\ndef `adds twice` = {\n  assert(double(double(1)) == 4)\n}\n"

let private shapes = "export module shapes\n\n@test(#io)\ndef `squares` = {\n  assert(3 * 3 == 9)\n}\n"

/// What `nanyx test`, with `options`, did for a package of `geometry` and `shapes`: its exit code, standard output and
/// standard error.
let private testing (options: string list) =
    use directory = testedPackage "" [ "src/geometry.nyx", geometry; "src/shapes.nyx", shapes ]
    let outcome = nanyxIn directory.Path ([ "test" ] @ options)
    outcome.ExitCode, outcome.StandardOutput.ReplaceLineEndings "\n", outcome.StandardError.ReplaceLineEndings "\n"

[<Fact>]
let ``nanyx test --list lists each test with where it is, and runs none`` () =
    let exitCode, output, _ = testing [ "--list" ]
    Assert.Equal(0, exitCode)
    Assert.Equal(
        "geometry.adds  src/geometry.nyx:6:1\ngeometry.`adds twice`  src/geometry.nyx:11:1\nshapes.squares (io)  src/shapes.nyx:4:1\n",
        output
    )

[<Fact>]
let ``nanyx test --list --format json writes a test object for each, with no outcome, and a summary`` () =
    let exitCode, output, _ = testing [ "--list"; "--format"; "json" ]
    Assert.Equal(0, exitCode)
    let objects = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun line -> JsonDocument.Parse(line).RootElement.Clone()) |> List.ofArray
    Assert.Equal<string list>([ "test"; "test"; "test"; "summary" ], objects |> List.map (fun element -> element.GetProperty("type").GetString()))
    let squares = objects.[2]
    Assert.Equal(("shapes.squares", "shapes", "squares", "src/shapes.nyx", 4, true),
                 (squares.GetProperty("name").GetString(), squares.GetProperty("module").GetString(), squares.GetProperty("test").GetString(),
                  squares.GetProperty("file").GetString(), squares.GetProperty("line").GetInt32(), squares.GetProperty("io").GetBoolean()))
    let mutable outcome = Unchecked.defaultof<JsonElement>
    Assert.False(squares.TryGetProperty("outcome", &outcome))

[<Fact>]
let ``nanyx test --test runs exactly the test it names, not one whose name contains it`` () =
    let exitCode, output, _ = testing [ "--test"; "geometry.adds" ]
    Assert.Equal(0, exitCode)
    Assert.Equal("Running 1 test in tested\n\nPASS geometry.adds\n\n1 passed\n", output)

[<Fact>]
let ``nanyx test --test takes several names, one on each line, as the report writes them or without backticks`` () =
    let exitCode, output, _ = testing [ "--test"; "geometry.`adds twice`\nshapes.squares"; "--list" ]
    Assert.Equal(0, exitCode)
    Assert.Equal("geometry.`adds twice`  src/geometry.nyx:11:1\nshapes.squares (io)  src/shapes.nyx:4:1\n", output)
    let _, unquoted, _ = testing [ "--test"; "geometry.adds twice"; "--list" ]
    Assert.Equal("geometry.`adds twice`  src/geometry.nyx:11:1\n", unquoted)

[<Fact>]
let ``nanyx test --module runs the tests of the module it names`` () =
    let exitCode, output, _ = testing [ "--module"; "geometry"; "--list" ]
    Assert.Equal(0, exitCode)
    Assert.Equal("geometry.adds  src/geometry.nyx:6:1\ngeometry.`adds twice`  src/geometry.nyx:11:1\n", output)

[<Fact>]
let ``A --test or --module that names nothing is an error, naming it`` () =
    let exitCode, output, errors = testing [ "--test"; "geometry.`adds thrice`" ]
    Assert.Equal(2, exitCode)
    Assert.Equal("", output)
    Assert.Contains("--test names geometry.`adds thrice`, which is no test of tested", errors)
    let exitCode, _, errors = testing [ "--module"; "circles" ]
    Assert.Equal(2, exitCode)
    Assert.Contains("--module names circles, which is no module of tested with tests", errors)

[<Fact>]
let ``nanyx test --list of a build that fails lists nothing, and says so`` () =
    use directory = testedPackage "" [ "src/geometry.nyx", geometry; "src/broken.nyx", "export module broken\n\n@test\ndef `adds` = {\n  assert(1 + == 2)\n}\n" ]
    let outcome = nanyxIn directory.Path [ "test"; "--list"; "--format"; "json" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.DoesNotContain("\"type\":\"test\"", outcome.StandardOutput)
    Assert.Contains("\"type\":\"summary\",\"built\":false", outcome.StandardOutput)
