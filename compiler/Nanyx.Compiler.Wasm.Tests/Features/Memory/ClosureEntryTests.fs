module TranspilerWasmTests.Features.Memory.ClosureEntry

open Xunit
open TranspilerWasmTestHelpers

// A function a closure is over borrows its environment, so a caller that holds the closure past the call counts it
// neither up nor down; a copy of the function that owns its environment, in the table slot after it, takes a closure
// that dies at a tail call (#1298).

let private repeat =
    """
def repeat: int, (int -> int), int -> int = { n, f, acc ->
  if n == 0 then acc else repeat(n - 1, f, f(acc))
}
"""

[<Fact>]
let ``Closure entry - a function that only calls a closure it is given borrows it, and the closure its environment`` () =
    // Not a tail call, which would pass the closure owned (see "Reference counting" in tail-calls.md).
    let source =
        repeat
        + """
def run: int -> int = { k -> repeat(10, { x -> x + k }, 0) + 1 }
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    let loop = functionText "repeat" wat
    Assert.Equal(0, countOccurrences "call $__nanyx_dup_fn" loop)
    Assert.Equal(0, countOccurrences "call $__nanyx_drop_fn" loop)
    Assert.Equal(1, countOccurrences "call_indirect" loop)
    let lambda = functionText "__lambda_0" wat
    Assert.Equal(0, countOccurrences "call $__nanyx_drop" lambda)

[<Fact>]
let ``Closure entry - a closure called in a loop with a capture on the heap runs, and memory balances`` () =
    let source =
        repeat
        + """
@entry
def main = {
  def suffix = "!"
  def f = { x -> x + string.length(suffix) }
  println(repeat(1000, f, 0))
  println(list.fold([1, 2, 3], 0, { acc, x -> acc + x * string.length(suffix) }))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1000\n6\n"

let private through =
    """
def apply: (int -> int), int -> int = { f, x -> f(x) }

def loop: int, int -> int = { n, acc ->
  if n == 0 then acc else
    def step = { m -> loop(m, acc + 1) }
    apply(step, n - 1)
}
"""

[<Fact>]
let ``Closure entry - a closure that dies at a tail call is passed to its owning entry, in the slot after it`` () =
    let wat = transpileWatNotInlined (through.ReplaceLineEndings "\n")
    let apply = functionText "apply" wat
    Assert.Contains("i32.load\n    i32.const 1\n    i32.add\n    return_call_indirect", apply.ReplaceLineEndings "\n")
    Assert.Equal(0, countOccurrences "call $__nanyx_drop" apply)
    Assert.Contains("$__lambda_0 $__lambda_0__owning", wat)

[<Fact>]
let ``Closure entry - a function with no closure that dies at a tail call has no owning entry`` () =
    let source =
        repeat
        + """
def run: int -> int = { k -> repeat(10, { x -> x + k }, 0) }
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.DoesNotContain("__owning", wat)

[<Fact>]
let ``Run - a million tail calls through closures that die there use no stack, and memory balances`` () =
    let source =
        through
        + """
@entry
def main = {
  println(loop(1000000, 0))
}
"""
    runSourceWithOptions true false "" (source.ReplaceLineEndings "\n") |> assertRan "1000000\n"
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1000000\n"
