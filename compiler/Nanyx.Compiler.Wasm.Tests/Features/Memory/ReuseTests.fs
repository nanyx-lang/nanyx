module TranspilerWasmTests.Features.Memory.Reuse

open Xunit
open TranspilerWasmTestHelpers

// A `match` arm that takes apart a value it holds the only reference to, and builds one of the same size, writes the
// new value into the old one's memory (see `CoreIrReuse`). The tests count the blocks the allocator hands out.

let private lists =
    normalized """def build: int -> list(int) = { n -> if n == 0 then [] else [n, ...build(n - 1)] }
def sum: list(int) -> int = {
  | [] -> 0
  | [x, ...rest] -> x + sum(rest)
}
def double: list(int) -> list(int) = {
  | [] -> []
  | [x, ...rest] -> [x * 2, ...double(rest)]
}
def built: int -> int = { n -> sum(build(n)) }
"""

[<Fact>]
let ``Reuse - mapping over a list held once rewrites its cells in place`` () =
    use program = instantiateWasm (lists + "def run: int -> int = { n -> sum(double(build(n))) }\n") []
    let buildAllocations, total = allocationsOf program "built" 100
    let mapAllocations, doubled = allocationsOf program "run" 100
    Assert.Equal(100, buildAllocations)
    // Building the list allocates each cell; doubling it allocates none.
    Assert.Equal(buildAllocations, mapAllocations)
    Assert.Equal(2 * total, doubled)

[<Fact>]
let ``Reuse - List.map with a closure over a list held once rewrites its cells in place`` () =
    let source = lists + "def run: int -> int = { n -> sum(list.map(build(n), { x -> x + 1 })) }\n"
    use program = instantiateWasm source []
    let buildAllocations, total = allocationsOf program "built" 100
    let mapAllocations, mapped = allocationsOf program "run" 100
    // As many blocks as building it: the closure captures nothing, so it is static data.
    Assert.Equal(buildAllocations, mapAllocations)
    Assert.Equal(total + 100, mapped)

[<Fact>]
let ``Reuse - mapping over a list still used afterwards leaves it as it was`` () =
    let source =
        lists +
        normalized """def run: int -> int = { n ->
  def xs = build(n)
  def ys = double(xs)
  sum(xs) * 1000 + sum(ys)
}
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 10
    Assert.Equal(20, allocations)
    Assert.Equal(55 * 1000 + 110, result)

[<Fact>]
let ``Reuse - a tuple taken apart and rebuilt is rewritten in place`` () =
    let source =
        normalized """def step: (int, (int, string)) -> (int, string) = { i, state ->
  match state
  | (count, label) -> (count + i, label)
}
def go: (int, (int, string)) -> int = { i, state ->
  if i == 0 then state.0 else go(i - 1, step(i, state))
}
def run: int -> int = { n -> go(n, (0, "total")) }
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 100
    // The record built at the start, and nothing after.
    Assert.Equal(1, allocations)
    Assert.Equal(5050, result)

[<Fact>]
let ``Reuse - a tag's cell taken apart and rebuilt is rewritten in place`` () =
    let source =
        normalized """def bump: #some(int) | #none -> #some(int) | #none = {
  | #some(n) -> #some(n + 1)
  | #none -> #none
}
def go: (int, #some(int) | #none) -> #some(int) | #none = { i, value ->
  if i == 0 then value else go(i - 1, bump(value))
}
def run: int -> int = { n ->
  match go(n, #some(0))
  | #some(total) -> total
  | #none -> -1
}
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 100
    Assert.Equal(1, allocations)
    Assert.Equal(100, result)

[<Fact>]
let ``Reuse - filtering a list held once keeps the cells of the elements it keeps`` () =
    let source = lists + "def run: int -> int = { n -> sum(list.filter(build(n), { x -> x % 3 == 0 })) }\n"
    use program = instantiateWasm source []
    let buildAllocations, _ = allocationsOf program "built" 100
    let liveBefore = program.LiveBlocks
    let filterAllocations, kept = allocationsOf program "run" 100
    // As many blocks as building it: the cells of the elements dropped are freed, and the others reused.
    Assert.Equal(buildAllocations, filterAllocations)
    Assert.Equal(List.sum [ 3..3..99 ], kept)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Reuse - an allocation in one branch of an if reuses the block, and the other branch frees it`` () =
    let source =
        lists +
        normalized """def evens: list(int), list(int) -> list(int) = {
  | [], acc -> acc
  | [x, ...rest], acc -> evens(rest, if x % 2 == 0 then [x, ...acc] else acc)
}
def run: int -> int = { n -> sum(evens(build(n), [])) }
"""
    use program = instantiateWasm source []
    let liveBefore = program.LiveBlocks
    let allocations, result = allocationsOf program "run" 100
    Assert.Equal(100, allocations)
    Assert.Equal(2550, result)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Reuse - partitioning a list held once reuses a cell in either branch`` () =
    let source =
        lists +
        normalized """def run: int -> int = { n ->
  def (small, large) = list.partition(build(n), { x -> x <= 10 })
  sum(small) * 10000 + sum(large)
}
"""
    use program = instantiateWasm source []
    let liveBefore = program.LiveBlocks
    let allocations, result = allocationsOf program "run" 100
    // Building the list, and the pair partition returns.
    Assert.Equal(101, allocations)
    Assert.Equal(55 * 10000 + (5050 - 55), result)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Reuse - reversing a list held once rewrites its cells in place`` () =
    let source = lists + "def run: int -> int = { n -> list.length(list.reverse(build(n))) }\n"
    use program = instantiateWasm source []
    let liveBefore = program.LiveBlocks
    let allocations, length = allocationsOf program "run" 100
    Assert.Equal(100, allocations)
    Assert.Equal(100, length)
    Assert.Equal(liveBefore, program.LiveBlocks)
