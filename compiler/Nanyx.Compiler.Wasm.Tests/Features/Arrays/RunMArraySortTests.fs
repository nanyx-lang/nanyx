module TranspilerWasmTests.Features.Arrays.RunMArraySort

open Xunit
open TranspilerWasmTestHelpers

// `MArray.sort` and its family sort a mutable array, or the run an `MSlice` views, in place, stably (#1897).

/// `body` run in a `memory` block of `main`, with `nanyx/marray` and `nanyx/mslice` imported.
let private sorting (body: string) =
    $"""import nanyx/marray
import nanyx/mslice

@entry
def main = {{
  memory {{
{body}
  }}
}}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - MArray.sort sorts empty, single, sorted, reversed and repeated arrays`` () =
    let source =
        sorting
            """    def empty: MArray(int) = MArray.fromList([])
    MArray.sort(empty)
    println(empty \MArray.toList)
    def one = MArray.fromList([7])
    MArray.sort(one)
    println(one \MArray.toList)
    def sorted = MArray.fromList([1, 2, 3, 4])
    MArray.sort(sorted)
    println(sorted \MArray.toList)
    def reversed = MArray.init(40, { i -> 40 - i })
    MArray.sort(reversed)
    println(reversed \MArray.toList == list.init(40, { i -> i + 1 }))
    def repeated = MArray.fromList([3, 1, 3, 2, 1])
    MArray.sort(repeated)
    println(repeated \MArray.toList)"""
    runSource source |> assertRan "[]\n[7]\n[1, 2, 3, 4]\ntrue\n[1, 1, 2, 3, 3]\n"

[<Fact>]
let ``Run - sortBy is stable, and the descending sorts keep equal keys in order`` () =
    let source =
        sorting
            """    def pairs = MArray.init(40, { i -> (i % 3, i) })
    MArray.sortBy(pairs, { p -> p.0 })
    def keys = pairs \MArray.toList \list.map { p -> p.0 }
    def stable = pairs \MArray.toList == (list.init(40, { i -> (i % 3, i) }) \list.sortBy { p -> p.0 })
    println("{keys == list.sort(keys)} {stable}")
    MArray.sortByDescending(pairs, { p -> p.0 })
    println(pairs \MArray.toList == (list.init(40, { i -> (i % 3, i) }) \list.sortByDescending { p -> p.0 }))
    def xs = MArray.fromList([2, 9, 4, 9, 1])
    MArray.sortDescending(xs)
    println(xs \MArray.toList)"""
    runSource source |> assertRan "true true\ntrue\n[9, 9, 4, 2, 1]\n"

[<Fact>]
let ``Run - sortWith sorts in a custom order`` () =
    let source =
        sorting
            """    def words = MArray.fromList(["pear", "fig", "banana", "kiwi"])
    MArray.sortWith(words, { a, b -> int.compare(string.length(a), string.length(b)) })
    println(words \MArray.toList)"""
    runSource source |> assertRan "[fig, pear, kiwi, banana]\n"

[<Fact>]
let ``Run - sorting a slice changes only the run it views`` () =
    let source =
        sorting
            """    def xs = MArray.fromList([9, 8, 7, 6, 5, 4, 3, 2, 1])
    MArray.slice(xs, 2, 6) \MSlice.sort
    println(xs \MArray.toList)
    def ys = MArray.init(50, { i -> 50 - i })
    def middle = MArray.slice(ys, 10, 40)
    MSlice.sortBy(middle, { y -> y })
    MSlice.sortDescending(MArray.slice(ys, 0, 5))
    println(ys \MArray.toList \list.take(12))
    println(middle \MSlice.toList == list.init(30, { i -> i + 11 }))"""
    runSource source |> assertRan "[9, 8, 4, 5, 6, 7, 3, 2, 1]\n[50, 49, 48, 47, 46, 45, 44, 43, 42, 41, 11, 12]\ntrue\n"

[<Fact>]
let ``Run - sorting an array of 100,000 elements`` () =
    let source =
        sorting
            """    def big = MArray.init(100000, { i -> (i * 7919) % 100003 })
    MArray.sort(big)
    def sorted = big \MArray.toList
    println(sorted == list.sort(sorted))
    println(MArray.at(big, 0))
    println(MArray.length(big))"""
    runSource source |> assertRan "true\n#some(0)\n100000\n"

let private sortingN =
    normalized """import nanyx/marray
import nanyx/mslice

def made: int -> int = { n ->
  memory {
    def xs = MArray.init(n, { i -> i * 7919 % 200003 })
    someOr(MArray.at(xs, 0), 0)
  }
}

def sorted: int -> int = { n ->
  memory {
    def xs = MArray.init(n, { i -> i * 7919 % 200003 })
    MArray.sort(xs)
    someOr(MArray.at(xs, 0), 0)
  }
}

def sortedBy: int -> int = { n ->
  memory {
    def xs = MArray.init(n, { i -> i * 7919 % 200003 })
    MArray.slice(xs, 0, n) \MSlice.sortBy { x -> 0 - x }
    someOr(MArray.at(xs, 0), 0)
  }
}
"""

[<Fact>]
let ``Sorting makes the same few blocks, whatever the array's length`` () =
    // #2212: the sort's scratch array was made from a list of every element, a block for each one. It is made by
    // filling a new array, so a sort of 100 elements and of 100,000 make the same blocks, the scratch among them.
    use program = instantiateWasm sortingN []
    let blocks (name: string) (n: int) =
        let made, _ = allocationsOf program "made" n
        let sorted, result = allocationsOf program name n
        sorted - made, result
    let small, smallest = blocks "sorted" 100
    let large, _ = blocks "sorted" 100000
    Assert.Equal(0, smallest)
    Assert.Equal(small, large)
    Assert.InRange(large, 1, 8)
    let smallBy, _ = blocks "sortedBy" 100
    let largeBy, _ = blocks "sortedBy" 100000
    Assert.Equal(smallBy, largeBy)
    Assert.InRange(largeBy, 1, 8)
