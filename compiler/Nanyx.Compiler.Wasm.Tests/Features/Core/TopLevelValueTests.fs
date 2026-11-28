/// A top-level value is built once, on its first read, and every read sees that value (#1257, #1151; see "Top-level
/// values" in docs/src/content/deep-dives/memory-management.md).
module TranspilerWasmTests.Features.Core.TopLevelValues

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A top-level value is built once however often it is read`` () =
    let source =
        """def x = dbg(1)

println(x)
println(x + 1)
"""
    runSource source |> withoutDbgLocations |> assertRan "1\n2\n1\n"

[<Fact>]
let ``A top-level value a function reads is built once across calls`` () =
    let source =
        """def x = dbg(10)

def next = { n -> n + x }

println(next(1))
println(next(2))
"""
    runSource source |> withoutDbgLocations |> assertRan "11\n12\n10\n"

[<Fact>]
let ``A generic top-level value is built once for each type it is read at`` () =
    let source =
        """def nothing: list(a) = dbg([])

def ints: list(int) = nothing
def moreInts: list(int) = nothing
def strings: list(string) = nothing

println(ints == moreInts)
println(strings == [])
"""
    // Each instance is built on its first read: `ints` builds the one at list(int), which `moreInts` reads.
    runSource source |> withoutDbgLocations |> assertRan "true\ntrue\n[]\n[]\n"

[<Fact>]
let ``A generic record value read again and again at one type is allocated once`` () =
    let source =
        """def box: (items: list(a), size: int) = (items = [], size = 0)

def sizes = { n: int ->
  def b: (items: list(int), size: int) = box
  b.size + n
}

println(sizes(1) + sizes(2) + sizes(3))
"""
    let wat = transpileWatWith checkedOptions source
    Assert.Contains("(func $box__int__build", wat)
    use reads = instantiateWat wat []
    let once = reads.Allocations
    use more =
        instantiateWat (transpileWatWith checkedOptions (source.Replace("sizes(3))", "sizes(3) + sizes(4) + sizes(5))"))) []
    // Two more reads of `box` allocate nothing more.
    Assert.Equal(once, more.Allocations)
    Assert.Equal(0, more.LiveBlocks)

[<Fact>]
let ``A top-level value on the heap is freed once the program has run`` () =
    let source =
        """def greeting = "hello" ++ ", world"

println(greeting)
println(greeting)
"""
    runSource source |> assertRan "hello, world\nhello, world\n"

[<Fact>]
let ``A value read through its getter is not inlined`` () =
    let source =
        """def x = dbg(1)

println(x)
"""
    let wat = transpileWat source
    Assert.Contains("(func $x__build", wat)
    Assert.Contains("global.get $x__built", wat)
    Assert.Contains("call $x\n", wat.ReplaceLineEndings "\n")

[<Fact>]
let ``A constant top-level value is inlined, with no getter`` () =
    let source =
        """def limit = 10

println(limit)
"""
    let wat = transpileWat source
    Assert.DoesNotContain("$limit__built", wat)

[<Fact>]
let ``Run - an empty list value, a list of undefined, is read as lists of different types`` () =
    let source =
        """def nothing = []

def f: list(int) -> int = { xs -> list.length(xs) }

def pair = (nothing, nothing)

def typed: (list(int), list(string)) = pair

println(f(nothing))
println(nothing ++ ["a"])
println([1] ++ nothing)
println(typed.0 ++ [2])
println(typed.1 ++ ["z"])
"""
    runSource source |> assertRan "0\n[a]\n[1]\n[2]\n[z]\n"
