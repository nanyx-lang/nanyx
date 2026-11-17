module TranspilerWasmTests.Features.Memory.MListSet

open Xunit
open TranspilerWasmTestHelpers

// `MList.set` changes the element of the cell it walks to, in place, and gives up the element it held (#1885). The
// tests count the blocks the allocator hands out, with memory checks on.

let private program =
    normalized """import nanyx/mlist

def built: int -> int = { n ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    MList.length(items)
  }
}

def setFirst: int, int -> int = { n, times ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    for 1..times | i -> MList.set(items, 0, i)
    someOr(MList.at(items, 0), 0) + MList.length(items)
  }
}

def setFirstOnce: int -> int = { n -> setFirst(n, 1) }

def setFirst100: int -> int = { n -> setFirst(n, 100) }

def setEach: int -> int = { n ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    for 1..n | i -> MList.set(items, i - 1, i * 2)
    someOr(MList.at(items, n - 1), 0)
  }
}

def words: int -> int = { n ->
  def xs = memory {
    def items = MList.empty()
    for 1..n | i -> items += "w{i}"
    MList.set(items, 0, "first")
    MList.set(items, n - 1, "last")
    MList.set(items, 0, "again")
    MList.toList(items)
  }
  match xs
    | [first, ...rest] -> string.length(first) * 100 + list.length(rest)
    | [] -> 0
}
"""

[<Fact>]
let ``MList - set at index 0 allocates nothing per call`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let onceAllocations, once = allocationsOf program "setFirstOnce" 100000
    Assert.Equal(1 + 100000, once)
    let setAllocations, result = allocationsOf program "setFirst100" 100000
    Assert.Equal(100 + 100000, result)
    Assert.Equal(onceAllocations, setAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MList - setting each element allocates nothing`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let buildAllocations, _ = allocationsOf program "built" 4000
    let setAllocations, last = allocationsOf program "setEach" 4000
    Assert.Equal(8000, last)
    Assert.Equal(buildAllocations, setAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MList - set gives up the element it replaces`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let _, result = allocationsOf program "words" 10
    // "again" at the front, and nine elements behind it; each replaced string is freed.
    Assert.Equal(500 + 9, result)
    Assert.Equal(liveBefore, program.LiveBlocks)
