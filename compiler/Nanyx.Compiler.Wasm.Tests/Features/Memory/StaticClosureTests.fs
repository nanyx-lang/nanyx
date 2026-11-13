module TranspilerWasmTests.Features.Memory.StaticClosure

open Xunit
open TranspilerWasmTestHelpers

// A closure that captures nothing (over a top-level function, a context member passed at a call, or a lambda that
// captures nothing) is static data: one block in the data segment with a count of 0, so making one allocates nothing
// (#743).

let private apply =
    """
def double: int -> int = { x -> x * 2 }

def apply: (int -> int), int -> int = { f, x -> f(x) }
"""

/// How many blocks `name(argument)` allocates, and what it returns.
let private allocationsOf (program: WasmProgram) (name: string) (argument: int) =
    let before = program.Allocations
    let result = program.Call(name, argument)
    program.Allocations - before, result

[<Fact>]
let ``Static closure - passing a top-level function as a value in a loop allocates nothing`` () =
    let source =
        apply
        + """
def go: int, int -> int = { i, acc -> if i == 0 then acc else go(i - 1, apply(double, acc) - acc + 1) }

def run: int -> int = { n -> go(n, 0) }
"""
    use program = instantiateWasm (source.ReplaceLineEndings "\n") []
    let allocations, result = allocationsOf program "run" 100
    Assert.Equal(0, allocations)
    Assert.Equal(100, result)

[<Fact>]
let ``Static closure - passing a lambda that captures nothing in a loop allocates nothing`` () =
    let source =
        apply
        + """
def go: int, int -> int = { i, acc -> if i == 0 then acc else go(i - 1, apply({ x -> x + 1 }, acc)) }

def run: int -> int = { n -> go(n, 0) }
"""
    use program = instantiateWasm (source.ReplaceLineEndings "\n") []
    let allocations, result = allocationsOf program "run" 100
    Assert.Equal(0, allocations)
    Assert.Equal(100, result)

[<Fact>]
let ``Static closure - every use of a top-level function as a value shares one block in the data segment`` () =
    let source =
        apply
        + """
def run: int -> int = { n -> apply(double, n) + apply(double, n + 1) }
"""
    let wat = transpileWat (source.ReplaceLineEndings "\n")
    Assert.Equal(0, countOccurrences "call $__nanyx_new_object" wat)
    // No string literals: the only data segment is the closure's.
    Assert.Equal(1, countOccurrences "(data " wat)

[<Fact>]
let ``Run - a top-level function passed to list.map maps the list, and memory balances`` () =
    let source =
        """
def double: int -> int = { x -> x * 2 }

@entry
def main = {
  def doubled = [1, 2, 3] \list.map(double)
  for doubled | x -> println(x)
  println(apply(double, 21))
}

def apply: (int -> int), int -> int = { f, x -> f(x) }
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n4\n6\n42\n"

[<Fact>]
let ``Run - context members passed to println on every call print, and memory balances`` () =
    let source =
        """
@entry
def main = {
  for [1, 2, 3] | i -> println("line {i}")
  println(true)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "line 1\nline 2\nline 3\ntrue\n"
