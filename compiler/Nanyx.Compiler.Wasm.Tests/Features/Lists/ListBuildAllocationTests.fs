module TranspilerWasmTests.Features.Lists.ListBuildAllocation

open Xunit
open TranspilerWasmTestHelpers

// `list.build` and `list.select` run their block under a handler of `Emit` that puts each value emitted in a new cell at
// the end of the list (#2566), so a `for` loop in the block stays a loop and nothing else is made per value: no
// sequence step, no closure for the rest. The benchmark is #2421's: over a list, each element emits itself, and the
// even ones emit themselves again. The tests count the blocks the allocator hands out, with memory checks on, and take
// away what making the input costs.

let private program =
    normalized """import nanyx/mlist

def input: int -> list(int) = { n -> list.init(n, { i -> i }) }

def initialised: int -> int = { n -> list.length(input(n)) }

def handWritten: int -> int = { n ->
  def xs = input(n)

  def ys = memory {
    def items = MList.empty()

    for xs | x ->
      items += x
      if x % 2 == 0 then items += x

    items \MList.toList
  }

  list.length(ys)
}

def selected: int -> int = { n ->
  def ys = input(n) \list.select { x ->
    emit(x)
    if x % 2 == 0 then emit(x)
  }

  list.length(ys)
}

def built: int -> int = { n ->
  def xs = input(n)

  def ys = list.build {
    for xs | x ->
      emit(x)
      if x % 2 == 0 then emit(x)
  }

  list.length(ys)
}
"""

[<Fact>]
let ``list.select and list.build make one cell for each value emitted, as building the list by hand does`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let input, count = allocationsOf program "initialised" 1000
    Assert.Equal(1000, count)
    let byHand, handLength = allocationsOf program "handWritten" 1000
    let selecting, selectLength = allocationsOf program "selected" 1000
    let building, buildLength = allocationsOf program "built" 1000
    Assert.Equal(1500, handLength)
    Assert.Equal(1500, selectLength)
    Assert.Equal(1500, buildLength)
    // A cell for each value, and the closures and mutable cells of the block, made once.
    Assert.InRange(selecting - input, 1500, 1510)
    Assert.InRange(building - input, 1500, 1510)
    Assert.InRange(selecting - input, 0, (byHand - input) * 103 / 100)
    Assert.InRange(building - input, 0, (byHand - input) * 103 / 100)
    Assert.Equal(liveBefore, program.LiveBlocks)
