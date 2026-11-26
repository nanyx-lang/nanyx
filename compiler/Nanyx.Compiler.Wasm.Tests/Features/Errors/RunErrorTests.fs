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
    // The code and headline, then the place, at the function, the line with the function underlined, and the body.
    Assert.Matches(
        "error NYX0301: Not supported by the WebAssembly backend\\r?\\n  --> .*:4:31\\r?\\n   \\|\\r?\\n 4 \\| def depth: Nest\\(int\\) -> int = \\{ t ->\\r?\\n   \\| {31}\\^+\\r?\\n   = It can't compile a recursive type that holds itself at other type arguments yet\\r?\\n$",
        outcome.StandardError)

[<Fact>]
let ``Run - a context's type parameter longer than one letter is reported where it is declared, and nothing runs`` () =
    let source =
        """context ToText(ab) = (totext: ab -> string)
type Point = (x: int, y: int)

def describe: [ToText(a)] a -> string = { value -> ToText(a).totext(value) }
def Point.totext: Point -> string = { p -> "p" }

println(describe(Point(x = 1, y = 2)))
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    assertDiagnostic "NYX0001" "Syntax error" ":1:16" "" outcome.StandardError
    Assert.Contains("'ab' cannot be", outcome.StandardError)
    Assert.DoesNotContain("NYX0301", outcome.StandardError)

[<Fact>]
let ``Run - a context on a type variable the type does not use is reported at the annotation, not by the backend`` () =
    let source =
        """
context Log = (log: string -> ())

def greet: [Log, ToString(a)] string -> () = { name -> log("hello {name}") }

def main = {
  use Log(log = { m -> println(m) })
  greet("Ada")
}

main()
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    assertDiagnostic "NYX0236" "Context on a type no parameter or result has 'ToString'" ":4:18" "" outcome.StandardError
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
    let outcome = runSource (withFinalNewline """type T = (a: Missing)
println("never")""")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    // The code and headline, then the place, at the type, then the body.
    assertDiagnostic "NYX0109" "Unknown type 'Missing'" ":1:14" "'Missing' is not in scope" outcome.StandardError
    Assert.DoesNotContain("deferred", outcome.StandardError)

[<Fact>]
let ``Run - a type error that is never reached does not stop the program`` () =
    let source =
        normalized """def unused = { x -> missing(x) }

println("fine")
"""
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("fine\n", outcome.StandardOutput)
    assertDiagnostic "NYX0102" "Unknown function 'missing'" ":1:21" "'missing' is not in scope" outcome.StandardError
    Assert.Contains("Running with 1 deferred error", outcome.StandardError)
    Assert.DoesNotContain("(deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - reaching a deferred error reports it and stops the program`` () =
    let source =
        normalized """def broken = { x -> missing(x) }

println("before")
broken(1)
println("after")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":1:21: error NYX0102: Unknown function 'missing': 'missing' is not in scope (deferred from compile time)", outcome.StandardError)
    // The error says why the program stopped: there is no trap report after it.
    Assert.DoesNotContain("Runtime error", outcome.StandardError)

[<Fact>]
let ``Run - a lambda that cannot be compiled because of a deferred error reports it when a function value calls it`` () =
    // The set can't be compiled, as the value set is not of the cell's type, so the lambda `memory` calls only reports
    // the error: it still takes and returns what its type says, as `memory` calls it through a function value. The
    // cell's type is written, so that the match is of a union, whose arms fit it (#2160).
    let source =
        normalized """import nanyx/mref

def cycle: int -> int = { n ->
  memory {
    def a: MRef(#some(value: int) | #none) = MRef.new(#none)
    MRef.set(a, #some((value = "{n}")))
    match MRef.get(a)
      | #some(x) -> x.value
      | #none -> 0
  }
}

@entry
def main = {
  println("{cycle(1)}")
}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains(":6:17: error NYX0201: Type mismatch: 'string' vs 'int' (deferred from compile time)", outcome.StandardError)
    Assert.DoesNotContain("Runtime error", outcome.StandardError)

/// A package whose @entry reaches a deferred error, with another file that has one it never reaches.
let private packageWithDeferredErrors =
    let main =
        normalized """module main

@entry
def main = {
  println("before")
  missing(1)
  println("after")
}
"""
    [ "main.nyx", main
      "other.nyx", "module other\n\ndef unused = { x -> nope }\n" ]

[<Fact>]
let ``Run - a package with deferred errors runs its entry`` () =
    let outcome, _ = nanyxOnPackage packageWithDeferredErrors [ "run"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains("Running with 2 deferred errors", outcome.StandardError)
    Assert.Contains("main.nyx:6:3: error NYX0102: Unknown function 'missing': 'missing' is not in scope (deferred from compile time)", outcome.StandardError)

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
    let outputPath = Path.ChangeExtension(inputPath, ".wasm")
    File.WriteAllText(inputPath, "def broken = { x -> missing(x) }\n")
    try
        let debug = nanyxWithInput "" [ "wasm"; inputPath; "-o"; outputPath ]
        Assert.Equal(0, debug.ExitCode)
        Assert.Contains("WebAssembly written with 1 deferred error", debug.StandardOutput)
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
        normalized """@entry
def main = {
  println("start")

  def y = nope
  def a = y + 1
  def b: string = y
  def c = y \contains("s")
  def d = y.length

  println("end")
}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":5:11: error NYX0101: Unknown identifier 'nope': 'nope' is not in scope (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - piping a value of no known type that did not stop the program stops it there`` () =
    let source =
        normalized """import nanyx/text

def g = { x -> if x == 0 then nope else x }

@entry
def main = {
  def y = g(5)
  println("before")
  def c = y \contains("s")
  println("never")
}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":9:11: error: the value used here has no known type, because of an error deferred from compile time", outcome.StandardError)

[<Fact>]
let ``Run - an unknown name given where an annotation's variable is expected stops the program there`` () =
    let source =
        normalized """def f: (string, a: type) -> a = { s, a ->
  def x: a = arts
  x
}

println("start")
println(f("s", a = type int))
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":2:14: error NYX0101: Unknown identifier 'arts'", outcome.StandardError)

[<Fact>]
let ``Run - ??? runs until it is reached, and stops the program there`` () =
    let source =
        normalized """def replace: string, string, string -> string = { s, pattern, replacement -> ??? }

println("start")
println(replace("a", "b", "c"))
println("never")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":1:78: error NYX0111: Not implemented: `???` stands for code not written yet (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - a use of ??? stops the program at the ???`` () =
    let source =
        normalized """println("start")
println(???.name + 1)
println("never")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":2:9: error NYX0111: Not implemented: `???` stands for code not written yet (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - interpolating a call to a function with a deferred error runs up to the call`` () =
    // `dist` cannot be typed, so neither can `a \dist`: the line before it still runs, and the call reports the error.
    let source =
        normalized """import (
  nanyx/math
)

type Point = (x: int, y: int)

def Point.dist = { p -> pythagoras(p.x, p.y) }

def a = Point(x = 5, y = 4)

println("x = {a.x}, y = {a.y}")
println("dist = {a \dist}")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("x = 5, y = 4\n", outcome.StandardOutput)
    Assert.Contains(":7:25: error NYX0102: Unknown function 'pythagoras': 'pythagoras' is not in scope (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - interpolating a value of no known type that does not stop the program stops it there`` () =
    let source =
        normalized """def g = { x -> if x == 0 then nope else x }

println("start")
println("g = {g(5)}")
println("never")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":4:15: error: the value interpolated here has no known type, because of an error deferred from compile time", outcome.StandardError)

[<Fact>]
let ``Run - a non-exhaustive match runs the cases it handles and traps on the rest`` () =
    let source =
        normalized """def name: #a | #b | #c -> string = { x ->
  match x
  | #a -> "a"
  | #b -> "b"
}

println(name(#a))
println(name(#b))
println(name(#c))
println("never")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("a\nb\n", outcome.StandardOutput)
    Assert.Contains(":2:3: error NYX0203: Non-exhaustive match '#c': Missing tags: c (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - each kind of deferred error traps where it was found`` () =
    let run body = runSource ("" + body + (withFinalNewline """
println("start")
println(f(1))"""))
    // A type mismatch, an unknown name inside a def, and a def whose annotation names an unknown type.
    for body, expected in
        [ """def f = { x -> if x == 1 then "one" else 2 }""", "Type mismatch"
          "def f = { x ->\n  def y = nope\n  \"{y}\"\n}", "Unknown identifier 'nope'"
          """def f: Missing -> string = { x -> "one" }""", "Unknown type 'Missing'" ] do
        let outcome = run body
        Assert.True((outcome.ExitCode = 1), $"Expected exit code 1 for {body}, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
        Assert.Equal("start\n", outcome.StandardOutput)
        Assert.Contains($"{expected}", outcome.StandardError)
        Assert.Contains("(deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - a def whose annotation is too general does not change another def's type variables of the same name`` () =
    // `broken`'s body makes its `b` a `#some(α)`. `mapError`'s `b` is its own, so its calls still compile (#453).
    let source =
        normalized """def broken: #some(α) | ω, (ω -> b) -> #some(α) | b = {
  | #some(x), _ -> #some(x)
  | other, f -> f(other)
}

def mapError: #some(α) | ω, (ω -> #some(α) | b) -> #some(α) | b = {
  | #some(x), _ -> #some(x)
  | other, f -> f(other)
}

def show: #some(int) | #bad(string) -> string = {
  | #some(n) -> "some {n}"
  | #bad(s) -> s
}

println(show(mapError(int.fromString("12"), { #invalidInt -> #bad("bad") })))
println(show(mapError(int.fromString("zz"), { #invalidInt -> #bad("bad") })))
"""
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("some 12\nbad\n", outcome.StandardOutput)
    assertDiagnostic "NYX0202" "Annotation too general 'broken'" ":1:5" "The annotation of 'broken' is more general than its inferred type" outcome.StandardError
    Assert.Contains("Running with 1 deferred error", outcome.StandardError)

[<Fact>]
let ``Run - a match with no matching arm exits 1 with a runtime error`` () =
    // A match on an int with no catch-all is a non-exhaustive match, so the trap is the deferred error.
    let source =
        normalized """def pick = { x ->
    match x
    | 1 -> 10
}

dbg(pick(2))
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("Non-exhaustive match '0': Missing int values, such as 0 (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - matching on a value of no known type that did not stop the program stops it there`` () =
    let source =
        normalized """def g = { x -> if x == 0 then nope else x }

println("start")
println(match g(5)
  | #a -> "a"
  | else -> "other")
println("never")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Contains(":4:15: error: the value matched here has no known type, because of an error deferred from compile time", outcome.StandardError)

[<Fact>]
let ``Run - a deferred error at a unit literal stops the program there, not running on with the literal`` () =
    // Inference reads a unit literal as another expression, which was written over the trap the error made (#2966).
    let source =
        normalized """dimension Distance
@baseUnit unit cm: Distance
dimension Time
@baseUnit unit s: Time

println("before")
def t: Time(int) = 2cm
println(t)
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":7:20: error NYX0201: Type mismatch: 'Distance(int)' vs 'Time(int)' (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``Run - a deferred error in a field's call stops the program there`` () =
    // A call of a record's field is read as a block that calls it, as a unit literal is read as another expression.
    let source =
        normalized """def r = (f = { x: int -> x + 1 })

println("before")
println(r.f("s"))
println("after")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains(":4:13: error NYX0201: Type mismatch: 'string' vs 'int' (deferred from compile time)", outcome.StandardError)
