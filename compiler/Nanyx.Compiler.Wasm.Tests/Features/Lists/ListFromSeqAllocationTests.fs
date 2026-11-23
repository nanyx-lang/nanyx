module TranspilerWasmTests.Features.Lists.ListFromSeqAllocation

open Xunit
open TranspilerWasmTestHelpers

// `list.fromSeq` (and `seq.toList`, which it is) makes one cell per element (#2421): the list it folds up backwards is
// unique, so `list.reverse` turns it round in the same cells. The tests count the blocks the allocator hands out, with
// memory checks on, and take away what pulling the sequence alone costs.

let private program =
    normalized """def pulled: int -> int = { n ->
  seq.map(1..n, { x -> x * 2 }) \seq.fold(0, { count, _ -> count + 1 })
}

def listed: int -> int = { n ->
  seq.map(1..n, { x -> x * 2 }) \list.fromSeq \list.length
}
"""

[<Fact>]
let ``list.fromSeq makes one cell for each element of the sequence`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let pulling, count = allocationsOf program "pulled" 1000
    Assert.Equal(1000, count)
    let listing, length = allocationsOf program "listed" 1000
    Assert.Equal(1000, length)
    Assert.Equal(1000, listing - pulling)
    Assert.Equal(liveBefore, program.LiveBlocks)
