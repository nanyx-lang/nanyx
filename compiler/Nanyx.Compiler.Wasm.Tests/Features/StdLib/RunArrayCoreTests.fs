module TranspilerWasmTests.Features.StdLib.RunArrayCore

open Xunit
open TranspilerWasmTestHelpers

// #2187: `Array` has the core collection functions and the ordered ones, with `list`'s names, argument order and
// meaning. Each is run on an array, `xs`, and on an empty one, `none`. A function that makes a new collection gives an
// array, made by index with no list on the way.

let private arrays = """def xs = Array[4, 7, 2, 8]
def none: Array(int) = Array[]
"""

let private runWithArrays (body: string) = runSource (arrays + body.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - array.filter and array.filterMap keep the elements they hold for`` () =
    runWithArrays
        """
def tens: int -> #some(int) | #skip = { x -> if x > 3 then #some(x * 10) else #skip }

println("{array.filter(xs, { x -> x > 3 })} {array.filter(none, { x -> x > 3 })}")
println("{array.filterMap(xs, tens)} {array.filterMap(none, tens)}")
println("{xs \filter({ x -> x > 100 })}")
"""
    |> assertRan "[4, 7, 8] []\n[40, 70, 80] []\n[]\n"

[<Fact>]
let ``Run - array.select gives what the function emits and array.selectOne the first it yields`` () =
    runWithArrays
        """
println("{array.select(xs, { x -> if x > 3 then emit(x) })} {array.select(none, { x -> emit(x) })}")
println("{array.select(Array[1, 2, 3, 4, 5, 6], { x -> for 1..x | i -> emit(i) })}")
println(array.selectOne(xs, { x -> if x < 4 then yield x }))
println(array.selectOne(xs, { x -> if x > 100 then yield x }))
println(array.selectOne(none, { x -> yield x }))
"""
    |> assertRan
        "[4, 7, 8] []\n[1, 1, 2, 1, 2, 3, 1, 2, 3, 4, 1, 2, 3, 4, 5, 1, 2, 3, 4, 5, 6]\n#some(2)\n#notFound\n#notFound\n"

[<Fact>]
let ``Run - array.find and array.findIndex give the first element that matches`` () =
    runWithArrays
        """
println(array.find(xs, { x -> x > 4 }))
println(array.find(xs, { x -> x == 9 }))
println(array.find(none, { x -> true }))
println(array.findIndex(xs, { x -> x == 2 }))
println(array.findIndex(xs, { x -> x == 3 }))
println(array.findIndex(none, { x -> true }))
"""
    |> assertRan "#some(7)\n#notFound\n#notFound\n#some(2)\n#notFound\n#notFound\n"

[<Fact>]
let ``Run - array.any, array.all, array.contains and array.isEmpty`` () =
    runWithArrays
        """
println((array.any(xs, { x -> x == 8 }), array.any(xs, { x -> x == 9 }), array.any(none, { x -> true })))
println((array.all(xs, { x -> x > 1 }), array.all(xs, { x -> x > 2 }), array.all(none, { x -> false })))
println((array.contains(xs, 7), array.contains(xs, 3), array.contains(none, 7)))
println((array.isEmpty(xs), array.isEmpty(none)))
"""
    |> assertRan "(true, false, false)\n(true, false, true)\n(true, false, false)\n(false, true)\n"

[<Fact>]
let ``Run - array.reverse and the sorts give a new array`` () =
    runWithArrays
        """
println("{array.reverse(xs)} {array.reverse(none)}")
println("{array.sort(xs)} {array.sort(none)}")
println("{array.sortBy(xs, { x -> 0 - x })} {array.sortBy(none, { x -> x })}")
println("{array.sortWith(xs, { a, b -> int.compare(b, a) })} {array.sortWith(none, int.compare)}")
println(xs)
"""
    |> assertRan "[8, 2, 7, 4] []\n[2, 4, 7, 8] []\n[8, 7, 4, 2] []\n[8, 7, 4, 2] []\n[4, 7, 2, 8]\n"

[<Fact>]
let ``Run - array.sortBy is stable, on an array longer than one sorted run`` () =
    runWithArrays
        """
def words = Array["xx", "bb", "a", "cc", "d"]
def keyed = array.init(40, { i -> (i % 3, i) })

println("{array.sortBy(words, string.length)}")
println("{array.sortBy(keyed, { (key, _) -> key }) \map({ (_, i) -> i })}")
"""
    |> assertRan
        "[a, d, xx, bb, cc]\n[0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36, 39, 1, 4, 7, 10, 13, 16, 19, 22, 25, 28, 31, 34, 37, 2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35, 38]\n"

[<Fact>]
let ``Run - array.take and array.skip clamp the count`` () =
    runWithArrays
        """
println("{array.take(xs, 2)} {array.skip(xs, 2)}")
println("{array.take(xs, 10)} {array.skip(xs, 10)}")
println("{array.take(xs, -1)} {array.skip(xs, -1)}")
println("{array.take(none, 1)} {array.skip(none, 1)}")
"""
    |> assertRan "[4, 7] [2, 8]\n[4, 7, 2, 8] []\n[] [4, 7, 2, 8]\n[] []\n"

[<Fact>]
let ``Run - array.zip pairs elements as long as the shorter array, and array.scan gives the running totals`` () =
    runWithArrays
        """
println("{array.zip(xs, Array["a", "b"])}")
println("{array.zip(xs, none)} {array.zip(none, xs)}")
println("{array.scan(xs, 0, { total, x -> total + x })} {array.scan(none, 0, { total, x -> total + x })}")
"""
    |> assertRan "[(4, a), (7, b)]\n[] []\n[0, 4, 11, 13, 21] [0]\n"

let private allocations =
    normalized """def filtered: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  array.length(array.filter(xs, { x -> x % 2 == 0 }))
}

def reversed: int -> int = { n ->
  def xs = array.init(n, { i -> i })
  array.length(array.reverse(xs))
}
"""

[<Fact>]
let ``Array - filter and reverse make as many blocks for 1,000 elements as for 10`` () =
    use program = instantiateWasm allocations []
    let liveBefore = program.LiveBlocks
    let fewFiltered, fewLength = allocationsOf program "filtered" 10
    let manyFiltered, manyLength = allocationsOf program "filtered" 1000
    Assert.Equal(5, fewLength)
    Assert.Equal(500, manyLength)
    Assert.Equal(fewFiltered, manyFiltered)
    Assert.Equal(fst (allocationsOf program "reversed" 10), fst (allocationsOf program "reversed" 1000))
    Assert.Equal(liveBefore, program.LiveBlocks)
