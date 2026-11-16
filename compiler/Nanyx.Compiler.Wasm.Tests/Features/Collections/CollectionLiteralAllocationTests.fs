module TranspilerWasmTests.Features.Collections.CollectionLiteralAllocation

open Xunit
open TranspilerWasmTestHelpers

// A collection literal's array is built in one block, its items written into it and each spread copied in, with no
// list of the items (#1641, see `CoreIrLowerArrayLiterals`). The tests count the blocks the allocator hands out, with
// memory checks on.

let private program =
    normalized """import nanyx/array

def items: int -> int = { n -> array.length(Array[n, n + 1, n + 2]) }

def spread: int -> int = { n ->
  def xs = Array[n, n]
  array.length(Array[...xs, 4])
}

def spreadList: int -> int = { n ->
  def ys = list.init(n, { i -> i })
  array.length(Array[0, ...ys, 0])
}

def strings: int -> int = { n ->
  def xs = Array["a {n}", "b"]
  def ys = Array["c", ...xs, "d", ...xs]
  array.length(ys) + array.length(xs)
}
"""

[<Fact>]
let ``Collection literals - an array literal is one block`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    Assert.Equal((1, 3), allocationsOf program "items" 5)
    Assert.Equal((2, 3), allocationsOf program "spread" 5)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Collection literals - a spread list is copied in as an array, with no cells of its own`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    // The list's ten cells, the array its elements are made into, and the literal's.
    Assert.Equal((12, 12), allocationsOf program "spreadList" 10)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Collection literals - spread strings are shared, each with a reference of its own`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    // The interpolated string and the two arrays.
    Assert.Equal((3, 8), allocationsOf program "strings" 5)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Run - a literal's items and spreads are in order`` () =
    let source = """
import nanyx/array

@entry
def main = {
  def xs = Array["x", "y"]
  def ys = ["p", "q"]
  println(Array["a", ...xs, "b", ...ys, ...xs])
  println(Array[...xs])
  def empty: Array(string) = Array[]
  println(Array[...empty, "only", ...empty])
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[a, x, y, b, p, q, x, y]\n[x, y]\n[only]\n"
