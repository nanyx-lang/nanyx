module TranspilerWasmTests.Features.Errors.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - what the backend cannot compile is reported with its place, like any other error`` () =
    let source =
        """
import nanyx/console

type Nest(a) = #leaf(a) | #deeper(Nest(list(a)))

def depth: Nest(int) -> int = { t ->
  def u = t
  1
}

@entry
def main = { println(depth(#leaf(1))) }
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    // `path/to/file.nyx:6:31: error NYX0301: ...`, at the function, then the line with the function underlined.
    Assert.Matches(
        ":6:31: error NYX0301: The WebAssembly backend cannot compile a recursive type that holds itself at other type arguments yet\\r?\\n  \\|\\r?\\n6 \\| def depth: Nest\\(int\\) -> int = \\{ t ->\\r?\\n  \\| {31}\\^+\\r?\\n$",
        outcome.StandardError)

[<Fact>]
let ``Run - a context's type parameter longer than one letter is reported where it is declared, and nothing runs`` () =
    let source =
        """import nanyx/console

context ToText(ab) = (totext: ab -> string)
type Point = (x: int, y: int)

def describe: [ToText(a)] a -> string = { value -> ToText(a).totext(value) }
def Point.totext: Point -> string = { p -> "p" }

println(describe(Point(x = 1, y = 2)))
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains(":3:16: error NYX0001: ", outcome.StandardError)
    Assert.Contains("'ab' cannot be", outcome.StandardError)
    Assert.DoesNotContain("NYX0301", outcome.StandardError)

[<Fact>]
let ``Run - a context on a type variable the type does not use is reported at the annotation, not by the backend`` () =
    let source =
        """
import nanyx/console

context Log = (log: string -> ())

def greet: [Log & ToString(a)] string -> () = { name -> log("hello {name}") }

def main = {
  use Log(log = { m -> println(m) })
  greet("Ada")
}

main()
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains(":6:19: error NYX0236: ", outcome.StandardError)
    Assert.DoesNotContain("NYX0301", outcome.StandardError)
    Assert.DoesNotContain("NYX0205", outcome.StandardError)

[<Fact>]
let ``Run - importing an unknown standard library module is a diagnostic`` () =
    let outcome = runSource "import nanyx/sockets\n\ndbg(1)\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("Unknown standard library module 'nanyx/sockets'", outcome.StandardError)

[<Fact>]
let ``Run - a compile error that cannot be deferred exits 1 with the diagnostic on stderr`` () =
    // An error in a type definition is not deferred to run time, so nothing runs.
    let outcome = runSource "import nanyx/console\n\ntype T = (a: Missing)\nprintln(\"never\")\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    // `file:line:col: error CODE: message`, at the type.
    Assert.Contains(":3:14: error NYX0109: Unknown type 'Missing'", outcome.StandardError)
    Assert.DoesNotContain("deferred", outcome.StandardError)

[<Fact>]
let ``Run - a type error that is never reached does not stop the program`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def unused = { x -> missing(x) }\n" +
        "\n" +
        "println(\"fine\")\n"
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("fine\n", outcome.StandardOutput)
    Assert.Contains(":3:21: error NYX0102: Unknown function 'missing'", outcome.StandardError)
    Assert.Contains("Running with 1 deferred error", outcome.StandardError)
    Assert.DoesNotContain("(deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - reaching a deferred error reports it and stops the program`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def broken = { x -> missing(x) }\n" +
        "\n" +
        "println(\"before\")\n" +
        "broken(1)\n" +
        "println(\"after\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":3:21: error NYX0102: Unknown function 'missing' (deferred from compile time)", outcome.StandardError)
    // The error says why the program stopped: there is no trap report after it.
    Assert.DoesNotContain("Runtime error", outcome.StandardError)

/// A package whose @entry reaches a deferred error, with another file that has one it never reaches.
let private packageWithDeferredErrors =
    [ "main.nyx", "module main\n\n@entry\ndef main = {\n  println(\"before\")\n  missing(1)\n  println(\"after\")\n}\n"
      "other.nyx", "module other\n\ndef unused = { x -> nope }\n" ]

[<Fact>]
let ``Run - a package with deferred errors runs its entry`` () =
    let outcome, _ = nanyxOnPackage packageWithDeferredErrors [ "run"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains("Running with 2 deferred errors", outcome.StandardError)
    Assert.Contains("main.nyx:6:3: error NYX0102: Unknown function 'missing' (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Build - a debug build writes a package with deferred errors, and a release build refuses it`` () =
    let debug, debugWrote = nanyxOnPackage packageWithDeferredErrors [ "build"; "{package}" ]
    Assert.Equal(0, debug.ExitCode)
    Assert.True(debugWrote)
    Assert.Contains("Built app (2 files) with 2 deferred errors", debug.StandardOutput)
    let release, releaseWrote = nanyxOnPackage packageWithDeferredErrors [ "build"; "--release"; "{package}" ]
    Assert.Equal(1, release.ExitCode)
    Assert.False(releaseWrote)
    Assert.Contains("Unknown function 'missing'", release.StandardOutput)

[<Fact>]
let ``Build - an error that cannot be deferred fails a debug build`` () =
    let outcome, wrote = nanyxOnPackage [ "main.nyx", "module main\n\ntype T = (a: Missing)\n\n@entry\ndef main = { println(\"hi\") }\n" ] [ "build"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.False(wrote)

[<Fact>]
let ``Wasm - a release build of a file refuses a deferred error`` () =
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-wasm-{Guid.NewGuid():N}.nyx")
    let outputPath = Path.ChangeExtension(inputPath, ".wat")
    File.WriteAllText(inputPath, "def broken = { x -> missing(x) }\n")
    try
        let debug = nanyxWithInput "" [ "wasm"; inputPath; "-o"; outputPath ]
        Assert.Equal(0, debug.ExitCode)
        Assert.Contains("WASM (WAT) emitted with 1 deferred error", debug.StandardOutput)
        File.Delete outputPath
        let release = nanyxWithInput "" [ "wasm"; inputPath; "-o"; outputPath; "--release" ]
        Assert.Equal(1, release.ExitCode)
        Assert.False(File.Exists outputPath)
    finally
        File.Delete inputPath
        File.Delete outputPath

[<Fact>]
let ``Run - a function whose body uses a value of no known type runs up to the error`` () =
    // The pipe and the member read need `y`'s type, which the error left unknown. They trap rather than keep the
    // whole of `main` from compiling, so `start` is printed before the program stops at `nope`.
    let source =
        "@entry\n" +
        "def main = {\n" +
        "  println(\"start\")\n" +
        "\n" +
        "  def y = nope\n" +
        "  def a = y + 1\n" +
        "  def b: string = y\n" +
        "  def c = y \\contains(\"s\")\n" +
        "  def d = y.length\n" +
        "\n" +
        "  println(\"end\")\n" +
        "}\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":5:11: error NYX0101: Unknown identifier 'nope' (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - piping a value of no known type that did not stop the program stops it there`` () =
    let source =
        "import nanyx/text\n" +
        "\n" +
        "def g = { x -> if x == 0 then nope else x }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def y = g(5)\n" +
        "  println(\"before\")\n" +
        "  def c = y \\contains(\"s\")\n" +
        "  println(\"never\")\n" +
        "}\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":9:11: error: the value used here has no known type, because of an error deferred from compile time", outcome.StandardError)

[<Fact>]
let ``Run - ??? runs until it is reached, and stops the program there`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def replace: string, string, string -> string = { s, pattern, replacement -> ??? }\n" +
        "\n" +
        "println(\"start\")\n" +
        "println(replace(\"a\", \"b\", \"c\"))\n" +
        "println(\"never\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":3:78: error NYX0111: Not implemented: `???` stands for code not written yet (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - interpolating a call to a function with a deferred error runs up to the call`` () =
    // `dist` cannot be typed, so neither can `a \dist`: the line before it still runs, and the call reports the error.
    let source =
        "import (\n" +
        "  nanyx/console\n" +
        "  nanyx/math\n" +
        ")\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "\n" +
        "def Point.dist = { p -> hypot(p.x, p.y) }\n" +
        "\n" +
        "def a = Point(x = 5, y = 4)\n" +
        "\n" +
        "println(\"x = {a.x}, y = {a.y}\")\n" +
        "println(\"dist = {a \\dist}\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("x = 5, y = 4\n", outcome.StandardOutput)
    Assert.Contains(":8:25: error NYX0102: Unknown function 'hypot' (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - interpolating a value of no known type that does not stop the program stops it there`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def g = { x -> if x == 0 then nope else x }\n" +
        "\n" +
        "println(\"start\")\n" +
        "println(\"g = {g(5)}\")\n" +
        "println(\"never\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":6:15: error: the value interpolated here has no known type, because of an error deferred from compile time", outcome.StandardError)

[<Fact>]
let ``Run - a non-exhaustive match runs the cases it handles and traps on the rest`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def name: #a | #b | #c -> string = { x ->\n" +
        "  match x\n" +
        "  | #a -> \"a\"\n" +
        "  | #b -> \"b\"\n" +
        "}\n" +
        "\n" +
        "println(name(#a))\n" +
        "println(name(#b))\n" +
        "println(name(#c))\n" +
        "println(\"never\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("a\nb\n", outcome.StandardOutput)
    Assert.Contains(":4:3: error NYX0203: Non-exhaustive match, missing tags: c (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - each kind of deferred error traps where it was found`` () =
    let run body = runSource ("import nanyx/console\n\n" + body + "\nprintln(\"start\")\nprintln(f(1))\n")
    // A type mismatch, an unknown name inside a def, and a def whose annotation names an unknown type.
    for body, expected in
        [ "def f = { x -> if x == 1 then \"one\" else 2 }", "Type mismatch"
          "def f = { x ->\n  def y = nope\n  \"{y}\"\n}", "Unknown identifier 'nope'"
          "def f: Missing -> string = { x -> \"one\" }", "Unknown type 'Missing'" ] do
        let outcome = run body
        Assert.True((outcome.ExitCode = 1), $"Expected exit code 1 for {body}, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
        Assert.Equal("start\n", outcome.StandardOutput)
        Assert.Contains($"{expected}", outcome.StandardError)
        Assert.Contains("(deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - a def whose annotation is too general does not change another def's type variables of the same name`` () =
    // `broken`'s body makes its `b` a `#some(α)`. `mapError`'s `b` is its own, so its calls still compile (#453).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def broken: #some(α) | ω, (ω -> b) -> #some(α) | b = {\n" +
        "  | #some(x), _ -> #some(x)\n" +
        "  | other, f -> f(other)\n" +
        "}\n" +
        "\n" +
        "def mapError: #some(α) | ω, (ω -> #some(α) | b) -> #some(α) | b = {\n" +
        "  | #some(x), _ -> #some(x)\n" +
        "  | other, f -> f(other)\n" +
        "}\n" +
        "\n" +
        "def show: #some(int) | #bad(string) -> string = {\n" +
        "  | #some(n) -> \"some {n}\"\n" +
        "  | #bad(s) -> s\n" +
        "}\n" +
        "\n" +
        "println(show(mapError(int.fromString(\"12\"), { #invalidInt -> #bad(\"bad\") })))\n" +
        "println(show(mapError(int.fromString(\"zz\"), { #invalidInt -> #bad(\"bad\") })))\n"
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("some 12\nbad\n", outcome.StandardOutput)
    Assert.Contains(":3:5: error NYX0202: Annotation of 'broken' is more general than the inferred type", outcome.StandardError)
    Assert.Contains("Running with 1 deferred error", outcome.StandardError)

[<Fact>]
let ``Run - a match with no matching arm exits 1 with a runtime error`` () =
    // A match on an int with no catch-all is a non-exhaustive match, so the trap is the deferred error.
    let source =
        "def pick = { x ->\n" +
        "    match x\n" +
        "    | 1 -> 10\n" +
        "}\n" +
        "\n" +
        "dbg(pick(2))\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("Non-exhaustive match, missing int values, such as 0 (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - matching on a value of no known type that did not stop the program stops it there`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def g = { x -> if x == 0 then nope else x }\n" +
        "\n" +
        "println(\"start\")\n" +
        "println(match g(5)\n" +
        "  | #a -> \"a\"\n" +
        "  | else -> \"other\")\n" +
        "println(\"never\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":6:15: error: the value matched here has no known type, because of an error deferred from compile time", outcome.StandardError)