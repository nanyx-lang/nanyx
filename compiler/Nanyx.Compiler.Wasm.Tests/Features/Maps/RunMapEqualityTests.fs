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
        [ """type $Loose = int"""
          """def $Loose.`==`: $Loose, $Loose -> bool = { a, b -> true }"""
          """def $Loose.hash: $Loose -> int = { a -> 0 }""" ]
        [ """[1 => $Loose(1)] == [1 => $Loose(2)]"""
          """[1 => $Loose(1)] == [2 => $Loose(1)]"""
          """[$Loose(1) => "a"] == [$Loose(2) => "a"]""" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - a function that requires Eq compares maps by their entries`` () =
    comparing
        [ """def same: [Eq(a)] a, a -> bool = { x, y -> x == y }"""
          """def sameMaps: [Eq(k) & Eq(v)] map(k, v), map(k, v) -> bool = { m, n -> m == n }""" ]
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
        """import nanyx/console

def build: list(string) -> map(string, list(string)) = { names ->
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
