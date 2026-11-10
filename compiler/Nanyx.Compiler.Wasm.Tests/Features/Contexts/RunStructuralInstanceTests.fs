module TranspilerWasmTests.Features.Contexts.RunStructuralInstances

open Xunit
open TranspilerWasmTestHelpers

/// A program that prints each of `values` on a line of its own, after the definitions in `definitions`.
let private printing (definitions: string list) (values: string list) =
    [ "import nanyx/console"
      ""
      yield! definitions
      ""
      "@entry"
      "def main = {"
      yield! values |> List.map (fun value -> $"  println(\"{{{value}}}\")")
      "}" ]
    |> String.concat "\n"

/// A program with an `==` attached to `$Ci` that holds for any two values, which prints whether each of `comparisons`
/// holds, one per line, after the definitions in `definitions`.
let private comparing (definitions: string list) (comparisons: string list) =
    printing
        [ "type $Ci = string"
          "def $Ci.`==`: $Ci, $Ci -> bool = { _, _ -> true }"
          "def a = $Ci(\"a\")"
          "def b = $Ci(\"b\")"
          yield! definitions ]
        comparisons

[<Fact>]
let ``Run - a hole writes a tag with a payload, a tuple, a record and a list part by part`` () =
    printing
        []
        [ "#some(\"hi\")"
          "#pair(1, \"a\")"
          "#rect(width = 1.0, height = 2.0)"
          "#done(())"
          "(x = 1, y = 2)"
          "(1, \"x\")"
          "((), 1)"
          "[1, 2, 3]"
          "[#some([(1, true)]), #some([])]" ]
    |> runSource
    |> assertRan "#some(hi)\n#pair(1, a)\n#rect(height = 2.0, width = 1.0)\n#done()\n(x = 1, y = 2)\n(1, x)\n((), 1)\n[1, 2, 3]\n[#some([(1, true)]), #some([])]\n"

[<Fact>]
let ``Run - a derived toString writes each part through its own ToString`` () =
    printing
        [ "type Point = (x: int, y: int)"
          "def Point.toString = { p -> \"<{p.x},{p.y}>\" }"
          "def show = { value -> \"{#some(value)}\" }"
          "def list: [ToString(a)] a -> string = { value -> \"{[value, value]}\" }"
          "def passed: [ToString(a)] a -> string = { value -> \"{value}\" }" ]
        [ "#at(Point(x = 1, y = 2))"
          "show(Point(x = 3, y = 4))"
          "show(5)"
          "list(Point(x = 5, y = 6))"
          "passed([#at(Point(x = 7, y = 8))])" ]
    |> runSource
    |> assertRan "#at(<1,2>)\n#some(<3,4>)\n#some(5)\n[<5,6>, <5,6>]\n[#at(<7,8>)]\n"

[<Fact>]
let ``Run - a value of every tag of a union is written by the one derived toString`` () =
    printing
        [ "def describe: int -> string = { n ->"
          "  def found = if n > 1 then #many(n, \"things\") else if n == 1 then #one(\"thing\") else #none"
          "  \"{found}\""
          "}" ]
        [ "describe(0)"; "describe(1)"; "describe(2)" ]
    |> runSource
    |> assertRan "#none\n#one(thing)\n#many(2, things)\n"

[<Fact>]
let ``Run - == uses the == attached to a type, and to a part of a structure`` () =
    comparing
        []
        [ "a == b"
          "a != b"
          "#some(a) == #some(b)"
          "#some(a) != #some(b)"
          "(a, 1) == (b, 1)"
          "(a, 1) == (b, 2)"
          "(n = a, m = [a]) == (n = b, m = [b])"
          "[a, a] == [b, b]"
          "[a, a] == [b]"
          "[#some((a, 1))] == [#some((b, 1))]" ]
    |> runSource
    |> assertRan "true\nfalse\ntrue\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\n"

[<Fact>]
let ``Run - a structure of a type variable is compared with the Eq the function requires`` () =
    comparing
        [ "def tagged: [Eq(a)] a, a -> bool = { x, y -> #some(x) == #some(y) }"
          "def listed: [Eq(a)] a, a -> bool = { x, y -> [x] != [y] }"
          "def paired = { x, y -> (x, 1) == (y, 1) }"
          "def same: [Eq(a)] a, a -> bool = { x, y -> x == y }" ]
        [ "tagged(a, b)"
          "tagged(1, 2)"
          "tagged(1, 1)"
          "listed(a, b)"
          "listed(1, 2)"
          "paired(a, b)"
          "paired(1, 2)"
          "same(#some(a), #some(b))"
          "same([1], [2])" ]
    |> runSource
    |> assertRan "true\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\nfalse\n"
