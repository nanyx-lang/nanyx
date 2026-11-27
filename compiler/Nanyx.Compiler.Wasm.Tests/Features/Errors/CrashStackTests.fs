/// A crash, an error deferred to run time and a runtime error print the stack under their message, a `name (file:line)`
/// frame on each line, innermost first, from the module's table of places (#2039, see `SpanTable`).
module TranspilerWasmTests.Features.Errors.CrashStacks

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers
open Transpiler.Wasm.CodeGen

/// A program that crashes three calls deep, under the top level.
let private threeDeep =
    """def inner = { n: int ->
  if n > 2 then crash("too big: {n}") else n
}

def middle = { n: int ->
  def m = inner(n) + 1
  m * 2
}

def outer = { n: int ->
  println("start")
  middle(n) + 1
}

println(outer(5))
"""

/// What a run of `threeDeep` writes to stderr: the crash, then each frame, `crash` itself first.
let private threeDeepStack =
    """crash: too big: 5
  crash (prelude.nyx:18)
  inner (main.nyx:2)
  middle (main.nyx:6)
  outer (main.nyx:12)
  <top level> (main.nyx:15)
"""

/// `stderr` with the line of the frame in the standard library's `crash` taken as written, so that the test doesn't
/// depend on the library's lines.
let private withCrashAt (stderr: string) =
    Text.RegularExpressions.Regex.Replace(stderr.ReplaceLineEndings "\n", @"crash \(prelude\.nyx:\d+\)", "crash (prelude.nyx:18)")

[<Fact>]
let ``A crash three calls deep prints each frame, innermost first`` () =
    let outcome = runSource (normalized threeDeep)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Equal(threeDeepStack, withCrashAt outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``Calls inlined into each other print the frames the calls would have had, at the lines they are written on`` () =
    let inlined = runSourceWithOptions true true "" (normalized threeDeep)
    let called = runSourceWithOptions true false "" (normalized threeDeep)
    Assert.Equal(threeDeepStack, withCrashAt inlined.StandardError)
    Assert.Equal(withCrashAt called.StandardError, withCrashAt inlined.StandardError)

[<Fact>]
let ``A release build keeps the table, so its crash prints the same stack`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" threeDeep
    let source = Path.Combine(directory.Path, "main.nyx")
    let program = Path.Combine(directory.Path, "main.wasm")
    let built = nanyxWithInput "" [ "wasm"; source; "--release"; "-o"; program ]
    Assert.Equal(0, built.ExitCode)
    use output = new MemoryStream()
    use error = new MemoryStream()
    let input = Path.Combine(directory.Path, "input.txt")
    File.WriteAllText(input, "")
    match Nanyx.Runtime.WasmRunner.runBinary (Nanyx.Runtime.Redirected(input, output, error, [])) [ "main" ] [] (File.ReadAllBytes program) with
    | Error(Nanyx.Runtime.Crashed(_, _, stack)) ->
        let printed = Text.Encoding.UTF8.GetString(error.ToArray()) + (stack |> List.map (fun frame -> $"  {frame}\n") |> String.concat "")
        Assert.Equal(threeDeepStack, withCrashAt printed)
    | other -> Assert.Fail $"%A{other}"

/// `source` run as the command line runs a program, reporting a crash's message to the runtime (see
/// `WasmOptions.CrashToRuntime`), and what it wrote to stderr.
let private runReporting (source: string) =
    let options = { WasmOptions.Default with Places = true; CrashToRuntime = true }
    let wasm = Transpiler.Wasm.WasmBinary.assemble (transpileWatWith options (normalized source))
    use directory = new TempDirectory()
    let input = Path.Combine(directory.Path, "input.txt")
    File.WriteAllText(input, "")
    use output = new MemoryStream()
    use error = new MemoryStream()
    let result = Nanyx.Runtime.WasmRunner.runBinary (Nanyx.Runtime.Redirected(input, output, error, [])) [ "main" ] [] wasm
    result, Text.Encoding.UTF8.GetString(error.ToArray())

[<Fact>]
let ``A crash reports its message to the runtime as data, and writes it to stderr as before`` () =
    match runReporting threeDeep with
    | Error(Nanyx.Runtime.Crashed(message, place, stack)), written ->
        Assert.Equal(Some "too big: 5", message)
        // Where the program called `crash`, its column too (#2950), not where `crash` is in the standard library: a
        // source compiled on its own is in a file with no name.
        Assert.Equal(Some("", 2, 17), place)
        Assert.Equal("crash: too big: 5\n", written.ReplaceLineEndings "\n")
        Assert.Equal<string list>([ "crash"; "inner"; "middle"; "outer"; "<top level>" ], stack |> List.map _.Name)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A failed orCrash is placed where the program called it, past the standard library's frames`` () =
    let source =
        "def find = { n: int ->\n  def found: #some(int) | #none = #none\n  found \\orCrash(\"no {n}\")\n}\n\nprintln(find(3))\n"
    match runReporting source with
    | Error(Nanyx.Runtime.Crashed(message, place, _)), _ ->
        Assert.Equal(Some "no 3", message)
        Assert.Equal(Some("", 3, 3), place)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A failed assert reports its message, and is placed at the line of the assert, as its message says`` () =
    match runReporting "def check = { n: int ->\n  assert(n < 2)\n  n\n}\n\nprintln(check(5))\n" with
    | Error(Nanyx.Runtime.Crashed(Some message, place, _)), written ->
        Assert.Contains("assert(n < 2)", message)
        Assert.Contains("n = 5", message)
        Assert.Equal(written, message)
        Assert.Equal(Some("", 2, 3), place)
        // The message names the place too, with its column; a source compiled on its own is in no file, so it names none.
        Assert.StartsWith("2:3: ", message)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``An error deferred to run time reports its message to the runtime`` () =
    match runReporting "def broken = { n: int ->\n  n + \"x\"\n}\n\nprintln(broken(1))\n" with
    | Error(Nanyx.Runtime.Crashed(Some message, place, _)), written ->
        // The column of the expression the error replaced, as its message says.
        Assert.Equal(Some("", 2, 3), place)
        Assert.Contains("NYX", message)
        Assert.StartsWith("2:3: ", message)
        Assert.Equal(written, message)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A module written to a file imports nothing to report a crash through`` () =
    let wat = transpileWatWith { WasmOptions.Default with Places = true } (normalized threeDeep)
    Assert.DoesNotContain("nanyx:crash", wat)

[<Fact>]
let ``An error deferred to run time prints the stack where it is reached`` () =
    let outcome =
        runSource (
            normalized
                """def broken = { n: int ->
  n + "x"
}

def caller = { n: int -> broken(n) }

println("before")
println(caller(1))
"""
        )
    Assert.Equal("before\n", outcome.StandardOutput)
    // The compiler reports the error before the run, and the program again where it reaches it, with the stack.
    let lines = outcome.StandardError.ReplaceLineEndings("\n").TrimEnd('\n').Split '\n'
    let reached = lines |> Array.findIndexBack (fun line -> line.EndsWith "(deferred from compile time)")
    Assert.Equal<string list>([ "  broken (main.nyx:2)"; "  caller (main.nyx:5)"; "  <top level> (main.nyx:8)" ], List.ofArray lines.[reached + 1 ..])
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A lambda's frame is named as one`` () =
    let outcome =
        runSource (
            normalized
                """def apply: (int -> int), int -> int = { f, n -> f(n) }

println(apply({ n -> 10 // n }, 0))
"""
        )
    Assert.Equal("Runtime error: division by zero\n  <lambda> (main.nyx:3)\n  apply (main.nyx:1)\n  <top level> (main.nyx:3)\n", outcome.StandardError.ReplaceLineEndings "\n")
