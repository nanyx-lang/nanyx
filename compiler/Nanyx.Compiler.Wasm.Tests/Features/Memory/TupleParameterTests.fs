module TranspilerWasmTests.Features.Memory.TupleParameter

open Xunit
open TranspilerWasmTestHelpers

// A function whose lone parameter is a tuple takes the tuple's elements, and its body has them as one tuple again. A
// field read from that tuple is the element it was made with, so a lambda that only reads elements of its tuple makes
// no tuple (#1884, see `CoreIrSimplify`). The tests count the blocks the allocator hands out.

let private pairs =
    normalized """def pairs: int, list((int, int)) -> list((int, int)) = { n, acc ->
  if n == 0 then acc else pairs(n - 1, [(n, n * 2), ...acc])
}
def built: int -> int = { n -> list.length(pairs(n, [])) }
"""

[<Fact>]
let ``Tuple parameter - a lambda that reads one element of its tuple parameter allocates nothing`` () =
    use program = instantiateWasm (pairs + "def evens: int -> int = { n -> list.length(list.filter(pairs(n, []), { p -> p.0 % 2 == 0 })) }\n") []
    let buildAllocations, _ = allocationsOf program "built" 100
    let filterAllocations, evens = allocationsOf program "evens" 100
    Assert.Equal(200, buildAllocations)
    // The pairs alone: the filter reuses the cells of the list it is given for the 50 it keeps.
    Assert.Equal(buildAllocations, filterAllocations)
    Assert.Equal(50, evens)

[<Fact>]
let ``Tuple parameter - a lambda that takes its tuple apart allocates nothing for it`` () =
    let source =
        pairs
        + "def firsts: int -> int = { n -> list.sum(list.map(pairs(n, []), { (a, _) -> a })) }\n"
        + "def ones: int -> int = { n -> list.sum(list.map(pairs(n, []), { _ -> 1 })) }\n"
    use program = instantiateWasm source []
    let firstsAllocations, total = allocationsOf program "firsts" 100
    // As many as a lambda that doesn't read its tuple: the pairs, and the list `map` makes.
    let onesAllocations, _ = allocationsOf program "ones" 100
    Assert.Equal(onesAllocations, firstsAllocations)
    Assert.Equal(5050, total)
