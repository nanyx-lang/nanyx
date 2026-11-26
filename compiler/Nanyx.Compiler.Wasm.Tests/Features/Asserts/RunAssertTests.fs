/// `assert` at run time (#2934): nothing when its condition holds; otherwise a crash with where it is, the `assert` as
/// written, and the values in it, printed as `dbg` prints them.
module TranspilerWasmTests.Features.Asserts.RunAssert

open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// The crash `source` stops at, which runs it up to an `assert` that fails: what it printed before, and its message
/// from where the `assert` is written, without the file's path and the stack.
let private failing (source: string) =
    let outcome = runSource (normalized source)
    Assert.Equal(1, outcome.ExitCode)
    let error = outcome.StandardError.ReplaceLineEndings "\n"
    let start = error.IndexOf "main.nyx:"
    Assert.True(start >= 0, error)
    // The header, then a line for each value shown, `  label = value`; the stack's lines after them are left out.
    let lines = error.Substring(start + "main.nyx:".Length).Split('\n')
    let shown = lines |> Array.skip 1 |> Array.takeWhile (fun line -> line.StartsWith "  " && line.Contains " = ")
    outcome.StandardOutput, (Array.append [| lines[0] |] shown |> Array.map (fun line -> line + "\n") |> String.concat "")

[<Fact>]
let ``Run - an assert that holds does nothing`` () =
    runSource (normalized "assert(1 + 1 == 2)\nassert(true)\nprintln(\"done\")\n") |> assertRan "done\n"

[<Fact>]
let ``Run - a failed comparison shows each side not written out in full, then each name it reads`` () =
    let source =
        """type Point = (x: int, y: int)

def scale: Point, int -> Point = { p, k -> Point(x = p.x * k, y = p.y * k + 1) }

def p = Point(x = 1, y = 2)
assert(scale(p, 2) == Point(x = 2, y = 4))
"""
    Assert.Equal(
        ("", "6:1: assert(scale(p, 2) == Point(x = 2, y = 4))\n  scale(p, 2) = (x = 2, y = 5)\n  p = (x = 1, y = 2)\n"),
        failing source)

[<Fact>]
let ``Run - a failed call shows the names it reads`` () =
    let source =
        """def n = 3
def isEven = { k: int -> k % 2 == 0 }
assert(isEven(n + 2))
"""
    Assert.Equal(("", "3:1: assert(isEven(n + 2))\n  n = 3\n"), failing source)

[<Fact>]
let ``Run - a failed name shows nothing more`` () =
    Assert.Equal(("", "2:1: assert(ready)\n"), failing "def ready = 1 > 2\nassert(ready)\n")

[<Fact>]
let ``Run - a failed pipe shows the names it reads, but not a lambda's parameter`` () =
    let source =
        """def xs = [1, -2]
assert(xs \list.all { x -> x > 0 })
"""
    Assert.Equal(("", "2:1: assert(xs \\list.all { x -> x > 0 })\n  xs = [1, -2]\n"), failing source)

[<Fact>]
let ``Run - a value whose type has no ToString is shown as dbg shows it`` () =
    let source =
        """type Secret = protected (code: int)

def s = Secret(code = 7)
assert(s.code == 8)
"""
    Assert.Equal(("", "4:1: assert(s.code == 8)\n  s.code = 7\n  s = Secret(code = 7)\n"), failing source)

[<Fact>]
let ``Run - a side is computed once, for the condition and the message`` () =
    let source =
        """def next: [Console] () -> int = { ->
  println("computed")
  2
}

def main: [Console] () -> () = { assert(next() == 3) }

main()
"""
    Assert.Equal(("computed\n", "6:34: assert(next() == 3)\n  next() = 2\n"), failing source)

[<Fact>]
let ``A release build keeps an assert`` () =
    let wat = transpileWatWith { WasmOptions.Default with Release = true } (normalized "def n = 3\nassert(n == 4)\n")
    Assert.Contains("assert(n == 4)", wat)

[<Fact>]
let ``Run - a failed assert names its file as a crash's place does, by its name in a script and from the root in a package`` () =
    // Not by its full path, which would show the machine's directories (#2969).
    let script = runSource (normalized "def n = 3\nassert(n == 4)\n")
    Assert.StartsWith("main.nyx:2:1: assert(n == 4)\n  n = 3\n", script.StandardError.ReplaceLineEndings "\n")
    let main = "module main\n\n@entry\ndef main = {\n  def n = 3\n  assert(n == 4)\n}\n"
    let package, _ = nanyxOnPackage [ "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal(1, package.ExitCode)
    Assert.StartsWith("src/main.nyx:6:3: assert(n == 4)\n  n = 3\n", package.StandardError.ReplaceLineEndings "\n")
    Assert.Contains("main (src/main.nyx:6)", package.StandardError)
