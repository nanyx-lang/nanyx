module TranspilerWasmTests.Features.Equality.Run

open Xunit
open TranspilerWasmTestHelpers


[<Fact>]
let ``Run - records and tuples compare field by field`` () =
    comparing
        [ "type Point = (x: int, y: int)" ]
        [ "(x = 1, y = 2) == (x = 1, y = 2)"
          "(x = 1, y = 2) == (x = 1, y = 3)"
          "Point(x = 1, y = 2) != Point(x = 1, y = 3)"
          "(1, s) == (1, \"aa\")"
          "(1, s) == (1, \"ab\")"
          "(f = 1.5, g = [2.0]) == (f = 1.5, g = [2.0])"
          "(f = 1.5, g = [2.0]) == (f = 1.5, g = [2.5])" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\n"

[<Fact>]
let ``Run - lists compare element by element`` () =
    comparing
        [ "def empty: list(int) = []" ]
        [ "[1, 2, 3] == [1, 2, 3]"
          "[1, 2] == [1, 2, 3]"
          "[1, 2, 3] == [1, 2]"
          "[[s], [\"b\"]] == [[\"aa\"], [\"b\"]]"
          "empty == [1]"
          "empty == []" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nF\nT\n"

[<Fact>]
let ``Run - tags compare by name, then payload by its type`` () =
    // A payload on the heap is compared by what it holds, not where it is (#526).
    comparing
        [ "type Point = (x: int, y: int)"
          "type Mixed = #i(int) | #s(string) | #p(Point) | #nothing"
          "def eqMixed: Mixed, Mixed -> bool = { a, b -> a == b }"
          "def eqResult: #ok(list(string)) | #error(string), #ok(list(string)) | #error(string) -> bool = { a, b -> a == b }" ]
        [ "#some(s) == #some(\"aa\")"
          "#some(s) == #some(\"ab\")"
          "eqMixed(#s(s), #s(\"aa\"))"
          "eqMixed(#p(Point(x = 1, y = 2)), #p(Point(x = 1, y = 2)))"
          "eqMixed(#s(s), #p(Point(x = 1, y = 2)))"
          "eqMixed(#nothing, #nothing)"
          "eqMixed(#nothing, #i(1))"
          "eqMixed(#i(2), #i(2))"
          "eqResult(#ok([s]), #ok([\"aa\"]))"
          "eqResult(#ok([s]), #error(\"aa\"))" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\nT\nT\nF\n"

[<Fact>]
let ``Each type compared gets one equality function, and its parts their own`` () =
    let wat =
        transpileWat (
            "def a: list((x: int, y: string)), list((x: int, y: string)) -> bool = { p, q -> p == q }\n" +
            "def b: list((x: int, y: string)), list((x: int, y: string)) -> bool = { p, q -> p != q }\n")
    Assert.Equal(1, countOccurrences "(func $__nanyx_equals_list_" wat)
    Assert.Equal(1, countOccurrences "(func $__nanyx_equals_rec_" wat)

[<Fact>]
let ``Run - a function that compares its type variable compares each type it is called with`` () =
    comparing
        [ "type Point = (x: int, y: int)"
          "def same = { x, y -> x == y }"
          "def differ = { x, y -> x != y }"
          "def holds = { xs, x -> xs \\list.any({ y -> y == x }) }"
          "def written: [Eq(a)] a, list(a) -> bool = { x, xs -> x != x or xs == [x] }" ]
        [ "same(1, 1)"
          "same(s, \"ab\")"
          "same(Point(x = 1, y = 2), Point(x = 1, y = 2))"
          "same([1.5], [1.5])"
          "same(#some(s), #some(\"aa\"))"
          "differ(1, 1)"
          "differ([s], [\"b\"])"
          "holds([1, 2, 3], 3)"
          "written(1, [1])"
          "list.distinct([s, \"aa\", \"b\"]) == [\"aa\", \"b\"]" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\nF\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - a local function compares at each type it is called with`` () =
    comparing
        [ "def both = { v, w ->"
          "  def same = { x, y -> x == y }"
          "  same(v, w)"
          "}" ]
        [ "both(1, 1)"
          "both(1, 2)"
          "both(#red, #red)" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - a local function compares strings, lists and tags on the heap by what they hold`` () =
    // One closure for each type it is used at, each comparing as that type does, and freeing what it is given (#616).
    """
import nanyx/console

type Shape = #circle(string) | #square(int)

def show = { b -> if b then "T" else "F" }

@entry
def main = {
  def s = "a" ++ "a"
  def same = { x, y -> x == y }
  def differ = { x, y -> x != y }
  def circle: Shape = #circle(s)
  def square: Shape = #square(1)
  println(show(same(s, "aa")))
  println(show(same(s, "ab")))
  println(show(same([s], ["aa"])))
  println(show(same([["a"], [s]], [["a"], ["aa"]])))
  println(show(same([["a"]], [["b"]])))
  println(show(same(#circle(s), #circle("aa"))))
  println(show(same(circle, square)))
  println(show(differ(s, "aa")))
  println(show(differ([s], ["b"])))
  println(show(same(1, 1)))
  println(show(same(2, 3)))
}
"""
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\nF\nT\nT\nF\n"

[<Fact>]
let ``Run - a function that compares its type variable uses the == attached to a type`` () =
    comparing
        [ "type $Email = string"
          "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }"
          "def same = { x, y -> x == y }"
          "def differ = { x, y -> x != y }" ]
        [ "same($Email(\"a\"), $Email(\"b\"))"
          "differ($Email(\"a\"), $Email(\"b\"))" ]
    |> runSource
    |> assertRan "T\nF\n"

[<Fact>]
let ``Run - comparing with a tag takes a value of any union that has it`` () =
    comparing
        [ "def isNone = { o -> o == #none }" ]
        [ "isNone(#none)"
          "isNone(#some(3))"
          "isNone(#some(s))" ]
    |> runSource
    |> assertRan "T\nF\nF\n"

[<Fact>]
let ``Run - a concrete comparison uses the == attached to a nominal type`` () =
    comparing
        [ "type $Point = (x: int, y: int)"
          "def $Point.`==`: $Point, $Point -> bool = { p, q -> p.x == q.x }" ]
        [ "$Point(x = 1, y = 2) == $Point(x = 1, y = 3)"
          "$Point(x = 1, y = 2) != $Point(x = 1, y = 3)"
          "$Point(x = 1, y = 2) == $Point(x = 2, y = 2)" ]
    |> runSource
    |> assertRan "T\nF\nF\n"

[<Fact>]
let ``Run - an Eq loaded with use changes a concrete comparison in its block`` () =
    comparing
        [ "def always: Eq(string) = (`==` = { a, b -> true })"
          "def loose = { a, b ->"
          "  use always"
          "  a == b"
          "}"
          "def literal = { a, b ->"
          "  use (`==` = { x, y -> false })"
          "  a == b"
          "}" ]
        [ "loose(\"ab\", \"cd\")"
          "loose(\"ab\", \"ab\")"
          "literal(\"ab\", \"ab\")"
          "\"ab\" == \"cd\"" ]
    |> runSource
    |> assertRan "T\nT\nF\nF\n"

[<Fact>]
let ``Run - a list of a type with its own == is compared through it, element by element`` () =
    comparing
        [ "type $Email = string"
          "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }"
          "def always: Eq(string) = (`==` = { a, b -> true })"
          "def loose: list(string), list(string) -> bool = { xs, ys ->"
          "  use always"
          "  xs == ys"
          "}"
          "def same: [Eq(a)] list(a), list(a) -> bool = { xs, ys -> xs == ys }" ]
        [ "[$Email(\"a\")] == [$Email(\"b\")]"
          "[$Email(\"a\")] != [$Email(\"b\")]"
          "[$Email(\"a\")] == [$Email(\"a\"), $Email(\"b\")]"
          "same([$Email(\"a\")], [$Email(\"b\")])"
          "loose([\"a\"], [\"b\"])"
          "[\"a\"] == [\"b\"]" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nT\nF\n"
