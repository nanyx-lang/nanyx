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

// Freeing a block too big for a size class of its own walks the large blocks' list, which is kept in address order. The
// strings and records programs make most of have classes of their own, so freeing many of them takes time in proportion
// to their number (#1464): eighty thousand strings of 38 bytes took 4.7 s to drop when they shared the large list.

[<Fact>]
let ``Heap reuse - dropping eighty thousand strings of 38 bytes takes time in proportion to their number`` () =
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
    let clock = System.Diagnostics.Stopwatch.StartNew()
    Assert.Equal(80000, program.Call("run", 80000))
    clock.Stop()
    Assert.True(clock.ElapsedMilliseconds < 1000L, $"80,000 strings took {clock.ElapsedMilliseconds} ms")
    Assert.Equal(0, program.LiveBlocks)
