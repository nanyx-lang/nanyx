module TranspilerWasmTests.Features.Memory.MListHandOver

open Xunit
open TranspilerWasmTestHelpers

// `MList.toList` hands over the cells the list built when nothing uses the `MList` afterwards, and copies them
// otherwise (see "Functional but in place" in the memory management page). `list.select` builds its list front to back
// the same way. The tests count the blocks the allocator hands out, with memory checks on.

let private program =
    normalized """import nanyx/mlist

def sum: list(int) -> int = {
  | [] -> 0
  | [x, ...rest] -> x + sum(rest)
}

def built: int -> int = { n ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    MList.count(items)
  }
}

def handed: int -> int = { n ->
  def xs = memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    items \toList
  }
  sum(xs)
}

def copied: int -> int = { n ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    def before = items \toList
    items += 1000
    def after = items \toList
    sum(before) * 100000 + sum(after)
  }
}

def selected: int -> int = { n ->
  sum(list.init(n, { i -> i }) \list.select { x -> if x % 2 == 0 then yield(x) })
}

def initialised: int -> int = { n -> sum(list.init(n, { i -> i })) }
"""

[<Fact>]
let ``MList - toList at the MList's last use allocates nothing for the list`` () =
    use program = instantiateWasm program []
    let buildAllocations, count = allocationsOf program "built" 100
    let liveBefore = program.LiveBlocks
    let handedAllocations, total = allocationsOf program "handed" 100
    Assert.Equal(100, count)
    Assert.Equal(5050, total)
    Assert.Equal(buildAllocations, handedAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MList - toList followed by more adds gives two lists, the first unchanged`` () =
    use program = instantiateWasm program []
    let buildAllocations, _ = allocationsOf program "built" 10
    let liveBefore = program.LiveBlocks
    let copiedAllocations, result = allocationsOf program "copied" 10
    Assert.Equal(55 * 100000 + 1055, result)
    // The first toList copies the ten cells, and the eleventh add makes a cell and a record; the second hands over.
    Assert.Equal(buildAllocations + 10 + 2, copiedAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``list.select builds its list without reversing it`` () =
    use program = instantiateWasm program []
    let initAllocations, _ = allocationsOf program "initialised" 100
    let liveBefore = program.LiveBlocks
    let selectAllocations, total = allocationsOf program "selected" 100
    Assert.Equal(2450, total)
    // A cell for each value yielded, and the cells, closure and mutable cells of the block: no copy of the list.
    Assert.InRange(selectAllocations - initAllocations, 50, 55)
    Assert.Equal(liveBefore, program.LiveBlocks)
