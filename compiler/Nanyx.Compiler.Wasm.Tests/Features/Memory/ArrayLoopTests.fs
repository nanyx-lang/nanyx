/// A `for` loop over an `Array` walks it by index, as `array.fold` does, rather than pulling the sequence `array.toSeq`
/// gives, which made four blocks for each element (#2207). The tests count the blocks the allocator hands out, and the
/// work the loop does against `array.fold`'s, with memory checks on.
module TranspilerWasmTests.Features.Memory.ArrayLoops

open Xunit
open TranspilerWasmTestHelpers

let private program =
    normalized """import nanyx/array

def made: int -> int = { n -> array.length(array.init(n, { i -> i })) }

def walked: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  memory {
    mut t = 0
    for xs | x -> t := t + x
    t
  }
}

def folded: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  array.fold(xs, 0, { acc, x -> acc + x })
}

def inStep: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  def ys = array.init(n, { i -> i * 2 })
  memory {
    mut t = 0
    for xs, ys | x, y -> t := t + x + y
    t
  }
}

def withRange: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  memory {
    mut t = 0
    for xs, 0.. | x, i -> t := t + x * i
    t
  }
}
"""

[<Fact>]
let ``A for loop over an array allocates nothing for its elements`` () =
    // The arrays themselves, and the `memory` block's own, the same for 10 elements as for 1,000: it made four blocks for
    // each element, 4,004 for 1,000.
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    Assert.Equal((2, 45), allocationsOf program "walked" 10)
    Assert.Equal((2, 499500), allocationsOf program "walked" 1000)
    // Two arrays in step, and an array in step with a range, are walked by index too.
    Assert.Equal((3, 135), allocationsOf program "inStep" 10)
    Assert.Equal((3, 1498500), allocationsOf program "inStep" 1000)
    Assert.Equal((2, 285), allocationsOf program "withRange" 10)
    Assert.Equal((2, 332833500), allocationsOf program "withRange" 1000)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``A for loop over an array does no more work than array.fold`` () =
    // 11,400,531 to `array.fold`'s 20,700,307 for 100,000 elements; pulling `array.toSeq` did 127,401,281.
    use program = instantiateWasm program []
    let walked = program.Work("walked", 100000)
    let folded = program.Work("folded", 100000)
    Assert.True(walked <= folded + folded / 5UL, $"walked: {walked}, folded: {folded}")
