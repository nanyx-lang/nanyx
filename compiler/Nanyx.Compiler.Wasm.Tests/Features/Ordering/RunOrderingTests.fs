module TranspilerWasmTests.Features.Ordering.Run

open Xunit
open TranspilerWasmTestHelpers

/// A `$Version` ordered by its attached `compare`: by major version, then minor.
let private version =
    [ "type $Version = (major: int, minor: int)"
      "def $Version.compare: $Version, $Version -> #less | #equal | #greater = { a, b ->"
      "  if a.major < b.major then #less"
      "  else if a.major > b.major then #greater"
      "  else Int.compare(a.minor, b.minor)"
      "}"
      "def v = { major, minor -> $Version(major = major, minor = minor) }"
      "def minor: $Version -> int = { x -> x.minor }" ]

/// The value of an option, or `fallback`.
let private orElse = "def orElse = { o, fallback -> match o | #some(x) -> x | _ -> fallback }"

/// Writes a list of ints, each followed by a space.
let private ints = "def ints = { xs -> List.fold(xs, \"\", { acc, x -> acc ++ \"{x} \" }) }"

[<Fact>]
let ``Run - strings are ordered by their bytes`` () =
    comparing
        []
        [ "\"apple\" < \"banana\""
          "\"b\" < \"abc\""
          "\"ab\" < \"abc\""
          "s <= \"aa\""
          "s > \"a\""
          "\"Z\" < \"a\""
          "\"é\" > \"z\"" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - tuples and lists are ordered position by position`` () =
    comparing
        [ "def empty: list(int) = []" ]
        [ "(1, \"b\") < (1, \"c\")"
          "(2, \"a\") < (1, \"z\")"
          "(1, s) >= (1, \"aa\")"
          "[1, 2] < [1, 2, 0]"
          "[3] <= [2, 9]"
          "empty < [0]"
          "[[s], [\"b\"]] > [[\"aa\"], [\"a\"]]"
          "(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0) > (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9)" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nT\nT\n"

[<Fact>]
let ``Run - float operators follow IEEE 754, and Float.compare puts NaN last`` () =
    comparing
        [ "import nanyx/math"
          "def isGreater = { o -> match o | #greater -> true | _ -> false }"
          "def isNaN = { x -> x != x }"
          orElse ]
        [ "1.5 < 2.5"
          "sqrt(0.0 - 1.0) < 1.0"
          "1.0 < sqrt(0.0 - 1.0)"
          "sqrt(0.0 - 1.0) >= sqrt(0.0 - 1.0)"
          "isGreater(Float.compare(sqrt(0.0 - 1.0), 1.0))"
          "List.take(List.sort([2.0, sqrt(0.0 - 1.0), 1.0]), 2) == [1.0, 2.0]"
          "isNaN(orElse(List.max([1.0, sqrt(0.0 - 1.0), 2.0]), 0.0))"
          "orElse(List.min([1.0, sqrt(0.0 - 1.0), 2.0]), 0.0) == 1.0" ]
    |> runSource
    |> assertRan "T\nF\nF\nF\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - a nominal type is ordered by the compare attached to it`` () =
    comparing
        [ yield! version; ints; orElse ]
        [ "v(1, 2) < v(1, 10)"
          "v(2, 0) >= v(1, 10)"
          "v(1, 1) <= v(1, 1)"
          "ints(List.map(List.sort([v(1, 10), v(1, 2), v(0, 5)]), minor)) == \"5 2 10 \""
          "minor(orElse(List.max([v(1, 10), v(2, 0), v(1, 2)]), v(0, 0))) == 0" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - sort, min and max order a list by the compiler's own Ord`` () =
    comparing
        [ orElse
          "def none: list(int) = []" ]
        [ "List.sort([3, 1, 2]) == [1, 2, 3]"
          "List.sort([\"pear\", \"apple\", s]) == [\"aa\", \"apple\", \"pear\"]"
          "List.sort([(2, \"a\"), (1, \"z\"), (1, \"b\")]) == [(1, \"b\"), (1, \"z\"), (2, \"a\")]"
          "List.sort([[2], [1, 5], [1]]) == [[1], [1, 5], [2]]"
          "orElse(List.min([4, 2, 8]), 0) == 2"
          "orElse(List.max([4, 2, 8]), 0) == 8"
          "orElse(List.max([\"b\", \"c\", \"a\"]), \"\") == \"c\""
          "orElse(List.min(none), -1) == -1"
          "List.sort([true, false]) == [false, true]" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - sortBy orders by a key of any type with an order, keeping ties in order`` () =
    comparing
        [ "def people = [(name = \"Cy\", age = 30), (name = \"Al\", age = 40), (name = \"Bo\", age = 30)]"
          "def names = { ps -> List.map(ps, { p -> p.name }) }" ]
        [ "names(List.sortBy(people, { p -> p.name })) == [\"Al\", \"Bo\", \"Cy\"]"
          "names(List.sortBy(people, { p -> p.age })) == [\"Cy\", \"Bo\", \"Al\"]"
          "names(List.sortBy(people, { p -> (p.age, p.name) })) == [\"Bo\", \"Cy\", \"Al\"]"
          "List.sortBy([\"bbb\", \"a\", \"cc\"], { w -> w }) == [\"a\", \"bbb\", \"cc\"]" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\n"

[<Fact>]
let ``Run - a function that orders its type variable orders each type it is called with`` () =
    comparing
        [ yield! version
          "def larger = { x, y -> if x > y then x else y }"
          "def written: [Ord(a)] a, list(a) -> bool = { x, xs -> x <= x and xs < [x] }" ]
        [ "larger(3, 7) == 7"
          "larger(\"y\", \"x\") == \"y\""
          "minor(larger(v(1, 2), v(1, 10))) == 10"
          "written(2, [1])"
          "written(v(1, 1), [v(0, 9)])" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - an Ord loaded with use changes how its block orders`` () =
    comparing
        [ "def backwards: Ord(string) = (compare = { a, b -> String.compare(b, a) })"
          "def sortedBackwards: list(string) -> list(string) = { xs ->"
          "  use backwards"
          "  List.sort(xs)"
          "}"
          "def lessBackwards: string, string -> bool = { a, b ->"
          "  use backwards"
          "  a < b"
          "}" ]
        [ "sortedBackwards([\"a\", \"c\", \"b\"]) == [\"c\", \"b\", \"a\"]"
          "lessBackwards(\"b\", \"a\")"
          "\"b\" < \"a\"" ]
    |> runSource
    |> assertRan "T\nT\nF\n"

[<Fact>]
let ``Each type ordered gets one ordering function, and its parts their own`` () =
    let wat =
        transpileWat (
            "def a: list((int, string)), list((int, string)) -> bool = { p, q -> p < q }\n" +
            "def b: list((int, string)), list((int, string)) -> bool = { p, q -> p >= q }\n")
    Assert.Equal(1, countOccurrences "(func $__nanyx_compare_list_" wat)
    Assert.Equal(1, countOccurrences "(func $__nanyx_compare_rec_" wat)
    Assert.Equal(1, countOccurrences "(func $__nanyx_compare_string" wat)

[<Fact>]
let ``Run - a list of a type with its own compare is ordered through it, element by element`` () =
    comparing
        [ yield! version
          "def before: [Ord(a)] list(a), list(a) -> bool = { xs, ys -> xs < ys }" ]
        [ "[v(1, 0), v(2, 0)] < [v(3, 0)]"
          "[v(3, 0)] <= [v(1, 0), v(2, 0)]"
          "[v(1, 5)] > [v(1, 2)]"
          "[v(1, 0)] >= [v(1, 0), v(0, 0)]"
          "before([v(1, 0)], [v(1, 0), v(0, 0)])"
          "before([v(2, 0)], [v(1, 9)])" ]
    |> runSource
    |> assertRan "T\nF\nT\nF\nT\nF\n"
