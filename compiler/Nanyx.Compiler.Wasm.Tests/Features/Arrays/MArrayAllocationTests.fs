module TranspilerWasmTests.Features.Arrays.MArrayAllocation

open Xunit
open TranspilerWasmTestHelpers

// Making a mutable array, copying one to and from an array, and copying a slice out, without a list in between
// (#1886): each array is one block, made at its length and filled in place. The tests count the blocks the allocator
// hands out, with memory checks on.

let private program =
    normalized """import (
  nanyx/marray
  nanyx/mslice
  nanyx/slice
)

def made: int -> int = { n ->
  memory {
    def xs = MArray.init(n, { i -> i })
    MArray.length(xs)
  }
}

def created: int -> int = { n ->
  memory {
    def xs: MArray(int) = MArray.create(n)
    MArray.length(xs)
  }
}

def source: int -> Array(int) = { n -> array.init(n, { i -> i }) }

def sourced: int -> int = { n -> array.length(source(n)) }

def copied: int -> int = { n ->
  def xs = source(n)
  def ys = memory {
    def m = MArray.fromArray(xs)
    MArray.set(m, 0, 7)
    MArray.toArray(m)
  }
  someOr(ys(0), 0) + someOr(xs(0), 0) + array.length(ys)
}

def sliced: int -> int = { n ->
  def xs = source(n)
  def ys = Slice.toArray(array.slice(xs, 1, n))
  array.fold(ys, 0, { total, x -> total + x })
}

def mslicedOnly: int -> int = { n ->
  memory {
    def m = MArray.init(n, { i -> i })
    def s = MArray.slice(m, 1, n)
    MSlice.length(s)
  }
}

def msliced: int -> int = { n ->
  memory {
    def m = MArray.init(n, { i -> i })
    def ys = MSlice.toArray(MArray.slice(m, 1, n))
    array.fold(ys, 0, { total, x -> total + x })
  }
}

def words: int -> int = { n ->
  def xs = array.init(n, { i -> "w{i}" })
  def ys = memory {
    def m = MArray.fromArray(xs)
    MArray.set(m, 0, "changed")
    MSlice.toArray(MArray.slice(m, 0, 2))
  }
  string.length(someOr(ys(0), "")) + string.length(someOr(xs(0), ""))
}
"""

[<Fact>]
let ``MArray - init and create make one block for the array`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    // The array, the box that puts it on a region, and the region of the `memory` block it is made in.
    let initAllocations, length = allocationsOf program "made" 1000
    Assert.Equal(1000, length)
    Assert.Equal(3, initAllocations)
    let createAllocations, length = allocationsOf program "created" 1000
    Assert.Equal(1000, length)
    Assert.Equal(3, createAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MArray - fromArray and toArray each make one block for the array`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let sourceAllocations, _ = allocationsOf program "sourced" 1000
    let copiedAllocations, result = allocationsOf program "copied" 1000
    Assert.Equal(7 + 0 + 1000, result)
    // The source array, then a block for the mutable array's array and one for its box, one for the array copied back,
    // and the region of the `memory` block.
    Assert.Equal(4, copiedAllocations - sourceAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Slice and MSlice - toArray makes one block`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let sourceAllocations, _ = allocationsOf program "sourced" 1000
    let slicedAllocations, total = allocationsOf program "sliced" 1000
    Assert.Equal(499500, total)
    // The source array, the slice and the copy.
    Assert.Equal(2, slicedAllocations - sourceAllocations)
    let mslicedOnlyAllocations, _ = allocationsOf program "mslicedOnly" 1000
    let mslicedAllocations, total = allocationsOf program "msliced" 1000
    Assert.Equal(499500, total)
    Assert.Equal(mslicedOnlyAllocations + 1, mslicedAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MArray - copies of strings keep a reference of their own to each`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let _, result = allocationsOf program "words" 10
    // "changed" in the copy back, and "w0" still in the array copied from.
    Assert.Equal(7 + 2, result)
    Assert.Equal(liveBefore, program.LiveBlocks)
