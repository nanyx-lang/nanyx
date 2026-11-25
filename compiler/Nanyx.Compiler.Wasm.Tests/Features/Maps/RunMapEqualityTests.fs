module TranspilerWasmTests.Features.Maps.RunEquality

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - maps with the same entries are equal, whatever order they were added in`` () =
    comparing
        []
        [ """["a" => 1, "b" => 2] == ["b" => 2, "a" => 1]"""
          """["a" => 1, "b" => 2] != ["b" => 2, "a" => 1]"""
          """map.insert(map.insert(map.empty, "x", 1), "y", 2) == map.insert(map.insert(map.empty, "y", 2), "x", 1)"""
          """map.remove(["a" => 1, "b" => 2], "a") == ["b" => 2]"""
          """[s => 1] == ["aa" => 1]""" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\n"

[<Fact>]
let ``Run - maps that differ in one value or one key are not equal`` () =
    comparing
        []
        [ """["a" => 1, "b" => 2] == ["a" => 1, "b" => 3]"""
          """["a" => 1, "b" => 2] != ["b" => 3, "a" => 1]"""
          """["a" => 1, "b" => 2] == ["a" => 1, "c" => 2]""" ]
    |> runSource
    |> assertRan "F\nT\nF\n"

[<Fact>]
let ``Run - maps of different sizes are not equal`` () =
    comparing
        [ """def none: map(string, int) = map.empty""" ]
        [ """["a" => 1, "b" => 2] == ["a" => 1]"""
          """["a" => 1] == ["a" => 1, "b" => 2]"""
          """none == ["a" => 1]"""
          """none == map.remove(["a" => 1], "a")""" ]
    |> runSource
    |> assertRan "F\nF\nF\nT\n"

[<Fact>]
let ``Run - nested maps, and records and lists that hold maps, compare the maps by their entries`` () =
    comparing
        [ """type Shop = (name: string, prices: map(string, int))""" ]
        [ """["x" => ["a" => 1, "b" => 2]] == ["x" => ["b" => 2, "a" => 1]]"""
          """["x" => ["a" => 1, "b" => 2]] == ["x" => ["b" => 3, "a" => 1]]"""
          """Shop(name = "s", prices = ["t" => 1, "c" => 2]) == Shop(name = "s", prices = ["c" => 2, "t" => 1])"""
          """Shop(name = "s", prices = ["t" => 1]) == Shop(name = "s", prices = ["t" => 2])"""
          """[["a" => 1, "b" => 2]] == [["b" => 2, "a" => 1]]"""
          """#some(["a" => 1, "b" => 2]) == #some(["b" => 2, "a" => 1])""" ]
    |> runSource
    |> assertRan "T\nF\nT\nF\nT\nT\n"

[<Fact>]
let ``Run - a map's values are compared by their own ==`` () =
    comparing
        [ """type Loose = protected int"""
          """def Loose.`==`: Loose, Loose -> bool = { a, b -> true }"""
          """def Loose.hash: Loose -> int = { a -> 0 }""" ]
        [ """[1 => Loose(1)] == [1 => Loose(2)]"""
          """[1 => Loose(1)] == [2 => Loose(1)]"""
          """[Loose(1) => "a"] == [Loose(2) => "a"]""" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - a function that requires Eq compares maps by their entries`` () =
    comparing
        [ """def same: [Eq(a)] a, a -> bool = { x, y -> x == y }"""
          """def sameMaps: [Eq(k), Eq(v)] map(k, v), map(k, v) -> bool = { m, n -> m == n }""" ]
        [ """same(["a" => 1, "b" => 2], ["b" => 2, "a" => 1])"""
          """sameMaps(["a" => 1, "b" => 2], ["b" => 2, "a" => 1])"""
          """sameMaps(["a" => 1], ["a" => 2])"""
          """list.length(list.distinct([["a" => 1, "b" => 2], ["b" => 2, "a" => 1]])) == 1""" ]
    |> runSource
    |> assertRan "T\nT\nF\nT\n"

[<Fact>]
let ``Run - sets with the same elements are equal, whatever order they were added in`` () =
    comparing
        []
        [ """set.fromList([1, 2, 3]) == set.fromList([3, 2, 1])"""
          """set.fromList([1, 2, 3]) == set.fromList([3, 2])"""
          """set.fromList([1, 2]) == set.fromList([1, 3])"""
          """set.fromList([s]) == set.insert(set.empty, "aa")"""
          """[set.fromList([1, 2])] == [set.fromList([2, 1])]""" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nT\n"

[<Fact>]
let ``Run - comparing maps and sets built at run time frees everything they hold`` () =
    let source =
        """def build: list(string) -> map(string, list(string)) = { names ->
  list.fold(names, map.empty, { m, name -> map.insert(m, name ++ "!", [name, name ++ name]) })
}

def describe = { b -> if b then "same" else "different" }

@entry
def main = {
  def a = build(["x", "y", "z"])
  def b = build(["z", "x", "y"])
  def c = build(["z", "x"])
  println(describe(a == b))
  println(describe(a == c))
  println(describe(set.fromList(map.keys(a)) == set.fromList(map.keys(b))))
}
"""
    source.ReplaceLineEndings "\n"
    |> runSourceWith true ""
    |> assertRan "same\ndifferent\nsame\n"

[<Fact>]
let ``Run - a map or a set beside a type nothing settles, as in a recursive type, is compared by its entries`` () =
    // The defs write no types, so an empty map or list in them leaves a variable nothing solves, which holds no value:
    // the map beside it is still compared by its entries, not by the order they were added in (#2522).
    comparing
        [ """type Node = #node(int, map(int, Node))"""
          """type Rec = #rec(map(int, string), list(Rec))"""
          """type Sets = #sets(set(int), list(Sets))"""
          """def leaf = #node(0, map.empty)""" ]
        [ """#node(1, map.empty \map.insert(1, leaf) \map.insert(2, leaf)) == #node(1, map.empty \map.insert(2, leaf) \map.insert(1, leaf))"""
          """#node(1, map.empty \map.insert(1, leaf)) == #node(1, map.empty \map.insert(2, leaf))"""
          """#rec(map.empty \map.insert(1, "a") \map.insert(2, "b"), []) == #rec(map.empty \map.insert(2, "b") \map.insert(1, "a"), [])"""
          """#sets(set.empty \set.insert(1) \set.insert(2), []) == #sets(set.empty \set.insert(2) \set.insert(1), [])"""
          """(map.empty \map.insert(1, "a") \map.insert(2, "b"), []) == (map.empty \map.insert(2, "b") \map.insert(1, "a"), [])""" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\n"

[<Fact>]
let ``Run - equal maps and sets have the same hash, however they were built, and can be keys`` () =
    comparing
        [ """def a = ["x" => 1, "y" => 2, "z" => 3]"""
          """def b = ["z" => 3, "w" => 9, "x" => 1] \map.remove("w") \map.insert("y", 2)"""
          """def p: set(int) = [1, 2, 3]"""
          """def q: set(int) = [3, 2, 1]"""
          """def byMap = [a => "first"]"""
          """def bySet = [p => "first"]""" ]
        [ """__hashOf(a) == __hashOf(b)"""
          """__hashOf(p) == __hashOf(q)"""
          """byMap(b) == #some("first")"""
          """bySet(q) == #some("first")"""
          """set.fromList([a, b]) \set.length == 1"""
          """__hashOf(a) == __hashOf(a \map.insert("q", 4))""" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\nF\n"

[<Fact>]
let ``Run - a map whose keys have their own == and hash is compared and hashed through them`` () =
    comparing
        [ """type Code = protected int"""
          """def Code.`==`: Code, Code -> bool = { Code(a), Code(b) -> a % 10 == b % 10 }"""
          """def Code.hash: Code -> int = { Code(a) -> a % 10 }"""
          """def a = [Code(11) => 1, Code(2) => 2]"""
          """def b = [Code(12) => 2, Code(1) => 1]""" ]
        [ """a == b"""
          """__hashOf(a) == __hashOf(b)"""
          """a == (b \map.insert(Code(3), 3))""" ]
    |> runSource
    |> assertRan "T\nT\nF\n"

[<Fact>]
let ``Run - maps built in different orders are equal inside records, lists and nominal types, through list.contains and a local function`` () =
    comparing
        [ """type Holder = protected (counts: map(string, int))"""
          """def a = ["x" => 1, "y" => 2, "z" => 3]"""
          """def b = ["z" => 3, "y" => 2, "x" => 1]"""
          """def same = { p, q -> p == q }""" ]
        [ """(counts = a, n = 1) == (counts = b, n = 1)"""
          """[a, a] == [b, b]"""
          """Holder(counts = a) == Holder(counts = b)"""
          """list.contains([["q" => 0], b], a)"""
          """same(a, b)"""
          """same(a, b \map.remove("x"))""" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\nF\n"
