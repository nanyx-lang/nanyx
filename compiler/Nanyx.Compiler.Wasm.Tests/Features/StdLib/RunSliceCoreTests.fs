module TranspilerWasmTests.Features.StdLib.RunSliceCore

open Xunit
open TranspilerWasmTestHelpers

// #2189: `Slice` has the core collection functions and the ordered ones, with `list`'s names, argument order and
// meaning. Each is run on a slice that starts after its array's start, `part`, and on an empty one, `none`, whose
// array has elements either side of it. A function that makes new elements gives an `Array`; `take` and `skip` give
// a slice of the same array.

let private slices = """import nanyx/slice

def part = Array[9, 4, 7, 2, 8, 3] \slice(1, 5)
def none = Array[9, 4, 7] \slice(2, 2)
"""

let private runWithSlices (body: string) = runSource (slices + body.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - Slice.fromArray, Slice.fromList and a Slice literal make a slice of a whole array`` () =
    runWithSlices
        """
def literal: Slice(int) = Slice[1, 2, 3]

println("{Slice.toArray(literal)}")
println("{Slice.toArray(Slice.fromArray(Array[4, 5]))}")
println("{Slice.toArray(Slice.fromList([6, 7]))}")
println(Slice.isEmpty(Slice.fromList([])))
"""
    |> assertRan "[1, 2, 3]\n[4, 5]\n[6, 7]\ntrue\n"

[<Fact>]
let ``Run - Slice.filter and Slice.filterMap keep the elements of the slice they hold for`` () =
    runWithSlices
        """
def tens: int -> #some(int) | #skip = { x -> if x > 3 then #some(x * 10) else #skip }

println("{Slice.filter(part, { x -> x > 3 })} {Slice.filter(none, { x -> x > 3 })}")
println("{Slice.filterMap(part, tens)} {Slice.filterMap(none, tens)}")
"""
    |> assertRan "[4, 7, 8] []\n[40, 70, 80] []\n"

[<Fact>]
let ``Run - Slice.select and Slice.selectOne give what the function yields`` () =
    runWithSlices
        """
println("{Slice.select(part, { x -> if x > 3 then yield x })} {Slice.select(none, { x -> yield x })}")
println(Slice.selectOne(part, { x -> if x < 4 then yield x }))
println(Slice.selectOne(part, { x -> if x > 100 then yield x }))
println(Slice.selectOne(none, { x -> yield x }))
"""
    |> assertRan "[4, 7, 8] []\n#some(2)\n#notFound\n#notFound\n"

[<Fact>]
let ``Run - Slice.find and Slice.findIndex count from the slice's start`` () =
    runWithSlices
        """
println(Slice.find(part, { x -> x < 5 }))
println(Slice.find(part, { x -> x == 9 }))
println(Slice.find(none, { x -> true }))
println(Slice.findIndex(part, { x -> x == 2 }))
println(Slice.findIndex(part, { x -> x == 3 }))
println(Slice.findIndex(none, { x -> true }))
"""
    |> assertRan "#some(4)\n#notFound\n#notFound\n#some(2)\n#notFound\n#notFound\n"

[<Fact>]
let ``Run - Slice.any, Slice.all and Slice.contains test only the elements the slice views`` () =
    runWithSlices
        """
println((Slice.any(part, { x -> x == 8 }), Slice.any(part, { x -> x == 9 }), Slice.any(none, { x -> true })))
println((Slice.all(part, { x -> x > 1 }), Slice.all(part, { x -> x > 2 }), Slice.all(none, { x -> false })))
println((Slice.contains(part, 7), Slice.contains(part, 3), Slice.contains(none, 7)))
"""
    |> assertRan "(true, false, false)\n(true, false, true)\n(true, false, false)\n"

[<Fact>]
let ``Run - Slice.reverse and the sorts give an array of the slice's elements`` () =
    runWithSlices
        """
println("{Slice.reverse(part)} {Slice.reverse(none)}")
println("{Slice.sort(part)} {Slice.sort(none)}")
println("{Slice.sortBy(part, { x -> 0 - x })} {Slice.sortBy(none, { x -> x })}")
println("{Slice.sortWith(part, { a, b -> int.compare(b, a) })} {Slice.sortWith(none, int.compare)}")
println("{Slice.toArray(part)}")
"""
    |> assertRan "[8, 2, 7, 4] []\n[2, 4, 7, 8] []\n[8, 7, 4, 2] []\n[8, 7, 4, 2] []\n[4, 7, 2, 8]\n"

[<Fact>]
let ``Run - Slice.sortBy is stable`` () =
    runWithSlices
        """
def words = Array["xx", "bb", "a", "cc", "d"] \slice(1, 5)

println("{Slice.sortBy(words, string.length)}")
"""
    |> assertRan "[a, d, bb, cc]\n"

[<Fact>]
let ``Run - Slice.take and Slice.skip give a slice of the same array, clamped`` () =
    runWithSlices
        """
println("{Slice.toArray(Slice.take(part, 2))} {Slice.toArray(Slice.skip(part, 2))}")
println("{Slice.toArray(Slice.take(part, 10))} {Slice.toArray(Slice.skip(part, 10))}")
println("{Slice.toArray(Slice.take(part, -1))} {Slice.toArray(Slice.skip(part, -1))}")
println("{Slice.toArray(Slice.take(none, 1))} {Slice.toArray(Slice.skip(none, 1))}")
println(Slice.at(Slice.skip(part, 1), 0))
"""
    |> assertRan "[4, 7] [2, 8]\n[4, 7, 2, 8] []\n[] [4, 7, 2, 8]\n[] []\n#some(7)\n"

[<Fact>]
let ``Run - Slice.zip pairs elements as long as the shorter slice, and Slice.scan gives the running totals`` () =
    runWithSlices
        """
println("{Slice.zip(part, Slice.skip(part, 1))}")
println("{Slice.zip(part, none)}")
println("{Slice.scan(part, 0, { total, x -> total + x })} {Slice.scan(none, 0, { total, x -> total + x })}")
"""
    |> assertRan "[(4, 7), (7, 2), (2, 8)]\n[]\n[0, 4, 11, 13, 21] [0]\n"

let private allocations =
    normalized """import nanyx/slice

def source: int -> Array(int) = { n -> array.init(n, { i -> i }) }

def sliced: int -> int = { n ->
  def s = Slice.fromArray(source(n))
  Slice.length(Slice.slice(Slice.slice(s, 10, n), 0, 500))
}

def takenAndSkipped: int -> int = { n ->
  def s = Slice.fromArray(source(n))
  Slice.length(Slice.take(Slice.skip(s, 10), 500))
}
"""

[<Fact>]
let ``Slice - take and skip copy no elements, as Slice.slice does`` () =
    use program = instantiateWasm allocations []
    let liveBefore = program.LiveBlocks
    let slicedAllocations, slicedLength = allocationsOf program "sliced" 1000
    let takenAllocations, takenLength = allocationsOf program "takenAndSkipped" 1000
    Assert.Equal(500, slicedLength)
    Assert.Equal(500, takenLength)
    Assert.Equal(slicedAllocations, takenAllocations)
    Assert.Equal(liveBefore, program.LiveBlocks)
