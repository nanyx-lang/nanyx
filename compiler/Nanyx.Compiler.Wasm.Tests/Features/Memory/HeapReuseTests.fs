module TranspilerWasmTests.Features.Memory.HeapReuse

open Xunit
open TranspilerWasmTestHelpers

// A string grown by `++` is copied at every step, and the copy before is freed. The heap reuses those copies, so it
// stays within a few copies of the string, not the sum of every copy there was.

[<Fact>]
let ``Heap reuse - a string grown by ++ twenty thousand times stays under a megabyte`` () =
    let source =
        normalized
            """import nanyx/text

def grow: int, string -> string = { n, acc ->
  if n == 0 then acc else grow(n - 1, acc ++ "ab")
}
def run: int -> int = { n -> string.length(grow(n, "")) }
"""
    use program = instantiateWasm source []
    Assert.Equal(40000, program.Call("run", 20000))
    Assert.True(program.MemoryBytes < 1024L * 1024L, $"the memory grew to {program.MemoryBytes} bytes")
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``Heap reuse - a string built from twenty thousand lines by ++ finishes in a few copies of its size`` () =
    let source =
        normalized
            """import nanyx/text

def build: int, string -> string = { n, acc ->
  if n == 0 then acc else build(n - 1, acc ++ "line {n} of the text, padded out\n")
}
def run: int -> int = { n -> string.length(build(n, "")) }
"""
    use program = instantiateWasm source []
    let length = program.Call("run", 20000)
    Assert.True(length > 20000 * 30)
    Assert.True(program.MemoryBytes < 8L * int64 length, $"the memory grew to {program.MemoryBytes} bytes for a {length}-byte string")
    Assert.Equal(0, program.LiveBlocks)

// The strings and records programs make most of have size classes of their own, so freeing many of them takes time in
// proportion to their number (#1464): eighty thousand strings of 38 bytes took 4.7 s to drop when they shared the
// large list, which was then kept in address order.

[<Fact>]
let ``Heap reuse - dropping eighty thousand strings of 38 bytes takes work in proportion to their number`` () =
    let source =
        normalized
            """def nums: int, list(int) -> list(int) = { n, acc ->
  if n == 0 then acc else nums(n - 1, [n, ...acc])
}
def total: list(string), int -> int = {
  | [], acc -> acc
  | [s, ...rest], acc -> total(rest, acc + 1)
}
def run: int -> int = { n -> total(list.map(nums(n, []), { i -> "a fairly long string, number {i}, here" }), 0) }
"""
    use program = instantiateWasm source []
    Assert.Equal(80000, program.Call("run", 80000))
    Assert.Equal(0, program.LiveBlocks)
    // In proportion, about 4 times the work of a quarter as many; walking the list would be 16 times or more.
    let atQuarter, atAll = float (program.Work("run", 20000)), float (program.Work("run", 80000))
    Assert.True(atAll < 6.0 * atQuarter, $"80,000 strings took {atAll} fuel, {atAll / atQuarter:F1} times the {atQuarter} of 20,000")
    Assert.Equal(0, program.LiveBlocks)

// A block too big for a size class goes on the large list, but `free` finds the free blocks either side of it by the
// size words at their ends (boundary tags), so it never walks the list (#1614). Each string here is built between two
// list cells, and the list is reversed in place, so that the strings are freed lowest first, none of them next to
// another: in a list kept in address order, each would have been put after all the others.

[<Fact>]
let ``Heap reuse - dropping eighty thousand strings of 300 bytes, apart, takes work in proportion to their number`` () =
    let source =
        normalized
            """def line: int -> string = { n ->
  "string {n}: this string is padded out past the largest size class, so that freeing it puts it on the list of large blocks, not a list of its own size; the list is long, as none of these strings is next to another, and each would have been put after all the others"
}
def strings: int, list(string) -> list(string) = { n, acc ->
  if n == 0 then acc else strings(n - 1, [line(n), ...acc])
}
def total: list(string), int -> int = {
  | [], acc -> acc
  | [s, ...rest], acc -> total(rest, acc + 1)
}
def run: int -> int = { n -> total(list.reverse(strings(n, [])), 0) }
"""
    use program = instantiateWasm source []
    Assert.Equal(80000, program.Call("run", 80000))
    Assert.Equal(0, program.LiveBlocks)
    // In proportion, about 4 times the work of a quarter as many; walking the list would be 16 times or more.
    let atQuarter, atAll = float (program.Work("run", 20000)), float (program.Work("run", 80000))
    Assert.True(atAll < 6.0 * atQuarter, $"80,000 strings took {atAll} fuel, {atAll / atQuarter:F1} times the {atQuarter} of 20,000")
    Assert.Equal(0, program.LiveBlocks)
