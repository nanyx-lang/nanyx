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
let ``nanyx test reports an error that can't be deferred and runs nothing`` () =
    let broken = "export module broken\n\n@test\ndef `adds` = {\n  assert(1 + == 2)\n}\n"
    let outcome = testing [] [ "src/broken.nyx", broken; "src/geometry.nyx", geometry ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX", outcome.StandardError)
    Assert.DoesNotContain("Running", outcome.StandardOutput)

[<Fact>]
let ``A type error in one module fails only the tests that reach it, and the others run (#2967)`` () =
    let broken =
        "export module broken\n\nexport def perimeter: int -> int = { n -> n * \"4\" }\n\nexport def area: int -> int = { n -> n * n }\n\n@test\ndef `areas` = {\n  assert(area(3) == 9)\n}\n\n@test\ndef `perimeters` = {\n  assert(perimeter(3) == 12)\n}\n"
    let doubles = "export module doubles\n\nexport def double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles a number` = {\n  assert(double(2) == 4)\n}\n"
    let outcome = testing [ "--seed"; "7" ] [ "src/broken.nyx", broken; "src/doubles.nyx", doubles ]
    Assert.Equal(1, outcome.ExitCode)
    let output = outcome.StandardOutput.ReplaceLineEndings "\n"
    let lines = output.Split('\n') |> List.ofArray
    Assert.Contains("Running 3 tests in tested with 1 deferred error", lines)
    Assert.Contains("PASS broken.areas", lines)
    Assert.Contains("FAIL broken.perimeters  src/broken.nyx:13:1", lines)
    Assert.Contains("PASS doubles.`doubles a number`", lines)
    Assert.Contains("  src/broken.nyx:3:43: error NYX0201: Type mismatch: 'string' is not a number, so it cannot be used with * (deferred from compile time)\n", output)
    Assert.Contains("2 passed, 1 failed", lines)
    Assert.Contains("NYX0201", outcome.StandardError)

[<Fact>]
let ``nanyx test says so when a package has no tests`` () =
    let outcome = testing [] [ "src/plain.nyx", "export module plain\n\nexport def one: int = 1\n" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("tested has no tests: mark a def @test to make it one\n", outcome.StandardOutput.ReplaceLineEndings "\n")

let private geometryTests =
    """module geometry.tests

import geometry as (double)

@test
def `doubles zero` = {
  assert(double(0) == 0)
}
"""

[<Fact>]
let ``nanyx test of a test file runs its tests, and only its, as part of its package`` () =
    use directory = testedPackage "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let outcome = nanyxIn directory.Path [ "test"; "tests/geometry_tests.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("Running 1 test in tested\n\nPASS geometry.tests.`doubles zero`\n\n1 passed\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx run of a test file says it is one, and to use nanyx test`` () =
    use directory = testedPackage "tests/**/*.nyx" [ "src/geometry.nyx", geometry; "tests/geometry_tests.nyx", geometryTests ]
    let outcome = nanyxIn directory.Path [ "run"; "tests/geometry_tests.nyx" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX0045", outcome.StandardError)
    Assert.Contains("It is a test file of tested, which its manifest's `tests` selects, and has no program: run its tests with `nanyx test tests/geometry_tests.nyx`", outcome.StandardError)

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

/// How many times `text` appears in `output`.
let private timesIn (output: string) (text: string) = (output.Split text).Length - 1

[<Fact>]
let ``nanyx test of a script reports each of its errors once, and counts it once (#3118)`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "def x: int = \"no\"\n\n@test\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n"
    let outcome = nanyxIn directory.Path [ "test"; "main.nyx" ]
    Assert.Equal(1, timesIn outcome.StandardError "error NYX0201")
    Assert.Contains("Running 1 test in main.nyx with 1 deferred error\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx test of a script reports a test that needs a context the test host lacks once (#3118)`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "@test\ndef `reads env directly` = {\n  println(variable(\"PATH\"))\n}\n"
    let outcome = nanyxIn directory.Path [ "test"; "main.nyx" ]
    Assert.Equal(1, timesIn outcome.StandardError "error NYX0312")

[<Fact>]
let ``nanyx test of a package reports each error of a module with tests once (#3118)`` () =
    let broken = "export module broken\n\ndef x: int = \"no\"\n\n@test\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n"
    let outcome = testing [] [ "src/broken.nyx", broken ]
    Assert.Equal(1, timesIn outcome.StandardError "error NYX0201")
    Assert.Contains("Running 1 test in tested with 1 deferred error\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``nanyx test shows a failed assert's place as its stack does, from the package's root`` () =
    // Not by the file's full path, which would show the machine's directories (#2969).
    let outcome = testing [] [ "src/geometry.nyx", geometry ]
    let output = outcome.StandardOutput.ReplaceLineEndings "\n"
    Assert.Contains("\n  src/geometry.nyx:13:3: assert(double(3) == 5)\n    double(3) = 6\n    geometry_halves (src/geometry.nyx:13)\n", output)

/// What `nanyx test` printed for the application `app` under the host `hostEntry` names, whose files, in `src`, are
/// `files`.
let private testingApplication (hostEntry: string) (files: (string * string) list) =
    use directory = new TempDirectory()
    directory.Write "app.nyxp" $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), {hostEntry})"""
    for path, text in files do
        directory.Write ("src/" + path) text
    nanyxIn directory.Path [ "test" ]

/// What `nanyx test` printed for an application under the console host that depends on `nanyx.fs`, whose files, by
/// their paths relative to its directory, are `files`.
let private testingWithFiles (files: (string * string) list) =
    use directory = new TempDirectory()
    directory.Write
        "app.nyxp"
        $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = [(package = "nanyx.fs", version = "{NyxCompiler.Toolchain.version}")], {consoleHostEntry})"""
    for path, text in files do
        directory.Write path text
    nanyxWithVariables [ "NANYX_TEST_GREETING", "hello" ] [ "test"; directory.Path ]

/// A module whose `#io` tests read a file in the package's directory and an environment variable, and one that prints
/// and fails, beside a test that isn't `#io`.
let private ioTests =
    "module main\n\nimport nanyx.fs/path as path\n\ndef load: [Async, FileSystem] string -> #some(string) | FsError = { at -> readFile(path.of(at)) }\n\n@test(#io)\ndef `reads the sample config` = {\n  assert(load(\"tests/sample.toml\") == #some(\"port = 8080\\n\"))\n}\n\n@test(#io)\ndef `reads the greeting` = {\n  assert(variable(\"NANYX_TEST_GREETING\") == #some(\"hello\"))\n}\n\n@test(#io)\ndef `prints and fails` = {\n  println(\"to the real console\")\n  assert(1 == 2)\n}\n\n@test\ndef `adds up` = {\n  assert(1 + 1 == 2)\n}\n"

[<Fact>]
let ``A test marked @test(#io) reads the package's files and the environment, and its output is still kept`` () =
    let outcome = testingWithFiles [ "src/main.nyx", ioTests; "tests/sample.toml", "port = 8080\n" ]
    Assert.Equal(1, outcome.ExitCode)
    let lines = outcome.StandardOutput.ReplaceLineEndings("\n").Split('\n') |> List.ofArray
    Assert.Contains("PASS main.`reads the sample config` (io)", lines)
    Assert.Contains("PASS main.`reads the greeting` (io)", lines)
    Assert.Contains("PASS main.`adds up`", lines)
    Assert.Contains("FAIL main.`prints and fails` (io)  src/main.nyx:18:1", lines)
    Assert.Contains("    to the real console", lines)

[<Fact>]
let ``A test build keeps the console host's prelude for the program's modules, so a module that reads its arguments and files runs its tests`` () =
    let main =
        "module main\n\ndef greeting: string -> string = { name -> \"hello {name}\" }\n\ndef exists: [Async, FileSystem] Path -> bool = { at -> pathExists(at) }\n\n@entry\ndef main = {\n  println(greeting(\"{args()}\"))\n}\n\n@test\ndef `greets` = {\n  assert(greeting(\"x\") == \"hello x\")\n}\n"
    let outcome = testingApplication consoleHostEntry [ "main.nyx", main ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("PASS main.greets", outcome.StandardOutput)

[<Fact>]
let ``A test build reports no entry point as unused, and still reports a private def nothing uses (#3094)`` () =
    let main =
        "module main\n\ndef spare = 1\n\n@entry\ndef main = {\n  println(\"hello\")\n}\n"
    let checks = "module checks\n\n@test\ndef `adds up` = {\n  assert(1 + 1 == 2)\n}\n"
    use directory = new TempDirectory()
    directory.Write
        "app.nyxp"
        $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"], tests = ["tests/**/*.nyx"]), {consoleHostEntry}, linters = [(package = "nanyx.lint", version = "{NyxCompiler.Toolchain.version}")])"""
    directory.Write "src/main.nyx" main
    directory.Write "tests/checks.nyx" checks
    let outcome = nanyxIn directory.Path [ "test" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.DoesNotContain("Unused private definition 'main'", outcome.StandardError)
    Assert.Contains("Unused private definition 'spare'", outcome.StandardError)

[<Fact>]
let ``A test build keeps the HTTP host's prelude for the program's modules, so a module that names Request runs its tests`` () =
    let main =
        "module main\n\ndef greeting: Request -> string = { request -> \"hello {Request.path(request)}\" }\n\n@entry\ndef main: Request -> Response = { request -> Response.text(200, greeting(request)) }\n\n@test\ndef `adds up` = {\n  assert(1 + 1 == 2)\n}\n"
    let outcome = testingApplication $"host = (package = \"nanyx.http.host\", version = \"{NyxCompiler.Toolchain.version}\")" [ "main.nyx", main ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("PASS main.`adds up`", outcome.StandardOutput)

[<Fact>]
let ``A test that crashes is reported with the file, line and column of the crash, once`` () =
    // `crash` writes only its message, so the report puts the crash's place in front of it (#2950); an `assert`'s
    // message starts with its place already, so it isn't written twice.
    let checks =
        "export module checks\n\n@test\ndef `crashes` = {\n  def n = 2\n  if n > 1 then crash(\"boom\") else ()\n}\n\n@test\ndef `asserts` = {\n  assert(1 + 1 == 3)\n}\n"
    let outcome = testing [ "--seed"; "1" ] [ "src/checks.nyx", checks ]
    Assert.Equal(1, outcome.ExitCode)
    let output = outcome.StandardOutput.ReplaceLineEndings "\n"
    Assert.Contains("FAIL checks.crashes  src/checks.nyx:4:1\n  src/checks.nyx:6:17: crash: boom\n", output)
    Assert.Contains("FAIL checks.asserts  src/checks.nyx:10:1\n  src/checks.nyx:11:3: assert(1 + 1 == 3)\n", output)

[<Fact>]
let ``A test that reaches a deferred error in a module it imports is reported with the error's place once`` () =
    // The trap's place is where the error is, with its column, which the deferred error's message starts with already
    // (#2950, #2967).
    let shapes = "export module shapes\n\nexport def perimeter: int -> int = { n -> n * \"4\" }\n"
    let checks = "export module checks\n\nimport shapes as (perimeter)\n\n@test\ndef `perimeters` = {\n  assert(perimeter(3) == 12)\n}\n"
    let outcome = testing [ "--seed"; "1" ] [ "src/shapes.nyx", shapes; "src/checks.nyx", checks ]
    Assert.Equal(1, outcome.ExitCode)
    let output = outcome.StandardOutput.ReplaceLineEndings "\n"
    let place = "src/shapes.nyx:3:43: "
    Assert.Contains(
        "FAIL checks.perimeters  src/checks.nyx:6:1\n  " + place
        + "error NYX0201: Type mismatch: 'string' is not a number, so it cannot be used with * (deferred from compile time)\n",
        output
    )
    Assert.Equal(1, (output.Split place).Length - 1)

[<Fact>]
let ``A test that isn't #io has the test host's Async, and runs code that starts and awaits tasks`` () =
    let tasks =
        "export module tasks\n\nexport def doubled: [Async] int -> int = { n ->\n  def task = start! n * 2\n  await(task)\n}\n\n@test\ndef `doubles in a task` = {\n  assert(doubled(21) == 42)\n}\n"
    let outcome = testing [] [ "src/tasks.nyx", tasks ]
    Assert.Equal("Running 1 test in tested\n\nPASS tasks.`doubles in a task`\n\n1 passed\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

/// A script whose `#io` test reads a real file, writes another and reads it back.
let private writingTest =
    "::install nanyx.fs\n\nimport (\n  nanyx.fs/file as file\n  nanyx.fs/path as path\n)\n\n@test(#io)\ndef `writes and reads back` = {\n  assert(file.read(path.of(\"given.txt\")) == #some(\"real\"))\n  assert(file.write(path.of(\"out.txt\"), \"hello\") == #ok)\n  assert(file.read(path.of(\"out.txt\")) == #some(\"hello\"))\n}\n"

[<Fact>]
let ``nanyx test --files-in-memory runs an #io test over the real files, and keeps what it writes off the disk (#3195)`` () =
    use directory = new TempDirectory()
    directory.Write "given.txt" "real"
    directory.Write "main.nyx" writingTest
    let outcome = nanyxIn directory.Path [ "test"; "--files-in-memory"; "main.nyx" ]
    Assert.True(outcome.ExitCode = 0, outcome.StandardOutput + outcome.StandardError)
    Assert.Contains("1 passed", outcome.StandardOutput)
    Assert.False(System.IO.File.Exists(System.IO.Path.Combine(directory.Path, "out.txt")))

    let written = nanyxIn directory.Path [ "test"; "main.nyx" ]
    Assert.True(written.ExitCode = 0, written.StandardOutput + written.StandardError)
    Assert.Equal("hello", System.IO.File.ReadAllText(System.IO.Path.Combine(directory.Path, "out.txt")))
