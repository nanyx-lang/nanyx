module TranspilerWasmTests.Features.Arrays.ArrayAllocation

open Xunit
open TranspilerWasmTestHelpers

// Making, mapping and comparing arrays without a list in between (#1548): each array is one block, made at its length
// and filled in place. The tests count the blocks the allocator hands out, with memory checks on.

let private program =
    normalized """import nanyx/array

def made: int -> int = { n -> array.length(array.init(n, { i -> i })) }

def created: int -> int = { n ->
  def xs: Array(int) = array.create(n)
  array.length(xs)
}

def mapped: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  array.fold(array.map(xs, { x -> x * 2 }), 0, { total, x -> total + x })
}

def compared: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  def ys = array.init(n, { i -> i })
  def zs = array.init(n + 1, { i -> i })
  (if xs == ys then 1 else 0) + (if xs == zs then 10 else 0)
}

def nothing: int -> int = { n -> n }
"""

[<Fact>]
let ``Arrays - init and create make one block`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    Assert.Equal((1, 1000), allocationsOf program "made" 1000)
    Assert.Equal((1, 1000), allocationsOf program "created" 1000)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Arrays - map makes one block for the array it gives`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    Assert.Equal((2, 999000), allocationsOf program "mapped" 1000)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Arrays - == compares without allocating, and arrays of different lengths are unequal`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    // The three arrays, and nothing for comparing them.
    Assert.Equal((3, 1), allocationsOf program "compared" 1000)
    Assert.Equal(liveBefore, program.LiveBlocks)
