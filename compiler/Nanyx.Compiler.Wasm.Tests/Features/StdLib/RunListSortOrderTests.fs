module TranspilerWasmTests.Features.StdLib.RunListSortOrder

open Xunit
open TranspilerWasmTestHelpers

// #1488: `list.sortDescending`, `list.sortByDescending` and `list.sortWith`.

[<Fact>]
let ``Run - sortByDescending is stable: elements with the same key keep their order`` () =
    runSource
        """
import nanyx/text

def people = [(name = "Ada", age = 36), (name = "Bob", age = 36), (name = "Cy", age = 20)]
def oldestFirst = people \list.sortByDescending { p -> p.age }

println(oldestFirst \list.map { p -> p.name } \string.join(", "))
"""
    |> assertRan "Ada, Bob, Cy\n"

[<Fact>]
let ``Run - sortDescending puts the greatest first, on ints and strings`` () =
    runSource
        """
def none: list(int) = []

println("{list.sortDescending([3, 1, 2, 3])} {list.sortDescending(["b", "c", "a"])} {list.sortDescending(none)}")
"""
    |> assertRan "[3, 3, 2, 1] [c, b, a] []\n"

[<Fact>]
let ``Run - sortWith sorts by one key ascending and another descending`` () =
    runSource
        """
import nanyx/text

def people = [(name = "Ada", age = 36), (name = "Cy", age = 20), (name = "Bob", age = 36), (name = "Di", age = 20)]
def byAgeThenNameDescending = list.sortWith(
  people
  { a, b ->
    match int.compare(a.age, b.age)
      | #equal -> string.compare(b.name, a.name)
      | other -> other
  }
)

println(byAgeThenNameDescending \list.map { p -> p.name } \string.join(", "))
println("{list.sortWith([3, 1, 2], { a, b -> int.compare(b, a) })}")
"""
    |> assertRan "Di, Cy, Bob, Ada\n[3, 2, 1]\n"

[<Fact>]
let ``Run - the sorts are stack-safe on a list of 100,000 elements`` () =
    runSource
        """
def big = list.init(100000, { i -> (i * 7919) % 100003 })
def descending = list.sortDescending(big)
def byKey = list.sortByDescending(big, { x -> x % 10 })
def with = list.sortWith(big, int.compare)

println("{list.head(descending)} {list.last(descending)} {list.length(byKey)} {list.head(with)}")
"""
    |> assertRan "#some(100002) #some(0) 100000 #some(0)\n"
