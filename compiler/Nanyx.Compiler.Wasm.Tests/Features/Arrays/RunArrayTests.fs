module TranspilerWasmTests.Features.Arrays.Run

open Xunit
open TranspilerWasmTestHelpers

/// A program that imports `nanyx/array` and prints each of `lines` in `main`. The tests run with memory checks on, so
/// an element an array does not give up is reported.
let private printing (lines: string list) =
    [ "import nanyx/array"
      ""
      "@entry"
      "def main = {"
      yield! lines |> List.map (fun line -> "  " + line)
      "}" ]
    |> String.concat "\n"

[<Fact>]
let ``Run - an array made from a list reads back its length, its elements and the list`` () =
    printing
        [ "def xs = array.fromList([10, 20, 30])"
          "println(array.length(xs))"
          "println(array.at(xs, 0))"
          "println(array.at(xs, 2))"
          "println(array.toList(xs))"
          "println(xs \\length)" ]
    |> runSource
    |> assertRan "3\n#some(10)\n#some(30)\n[10, 20, 30]\n3\n"

[<Fact>]
let ``Run - an index out of range is none, never a trap`` () =
    printing
        [ "def xs = array.fromList([10, 20, 30])"
          "println(array.at(xs, 3))"
          "println(array.at(xs, -1))"
          "println(array.at(xs, 9223372036854775807))" ]
    |> runSource
    |> assertRan "#none\n#none\n#none\n"

[<Fact>]
let ``Run - an empty array has no elements`` () =
    printing
        [ "def empty: Array(int) = array.fromList([])"
          "println(array.length(empty))"
          "println(array.at(empty, 0))"
          "println(array.toList(empty))"
          "println(empty)"
          "println(array.fold(empty, 7, { total, x -> total + x }))" ]
    |> runSource
    |> assertRan "0\n#none\n[]\n[]\n7\n"

[<Fact>]
let ``Run - an array of strings gives each element up once`` () =
    printing
        [ "def names = array.fromList([\"a\" ++ \"b\", \"c\" ++ \"d\"])"
          "println(array.at(names, 1) ?? \"\")"
          "println(names \\map({ name -> name ++ \"!\" }))"
          "println(array.toList(names))"
          "println(names)" ]
    |> runSource
    |> assertRan "cd\n[ab!, cd!]\n[ab, cd]\n[ab, cd]\n"

[<Fact>]
let ``Run - init, map and fold`` () =
    printing
        [ "def squares = array.init(4, { i -> i * i })"
          "println(squares)"
          "println(squares \\map({ n -> n + 1 }))"
          "println(array.fold(squares, 0, { total, n -> total + n }))"
          "println(array.init(0, { i -> i }))" ]
    |> runSource
    |> assertRan "[0, 1, 4, 9]\n[1, 2, 5, 10]\n14\n[]\n"

[<Fact>]
let ``Run - create fills each slot with its type's default`` () =
    printing
        [ "def ints: Array(int) = array.create(3)"
          "def strings: Array(string) = array.create(2)"
          "def none: Array(int) = array.create(-1)"
          "println(ints)"
          "println(strings \\map({ s -> \"<{s}>\" }))"
          "println(array.length(none))" ]
    |> runSource
    |> assertRan "[0, 0, 0]\n[<>, <>]\n0\n"

[<Fact>]
let ``Run - arrays are equal when their elements are, in order`` () =
    printing
        [ "def show = { b -> if b then \"T\" else \"F\" }"
          "def xs = array.fromList([\"a\" ++ \"a\", \"b\"])"
          "println(show(xs == array.fromList([\"aa\", \"b\"])))"
          "println(show(xs == array.fromList([\"b\", \"aa\"])))"
          "println(show(xs != array.fromList([\"aa\"])))"
          "println(show((items = xs) == (items = array.fromList([\"aa\", \"b\"]))))"
          "println(show(array.fromList([array.fromList([1])]) == array.fromList([array.fromList([1])])))" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\n"

[<Fact>]
let ``Run - arrays of records, tags, floats and narrow integers`` () =
    printing
        [ "def points = array.fromList([(x = 1, y = 2), (x = 3, y = 4)])"
          "println(array.at(points, 1))"
          "def options = array.fromList([#some(\"x\" ++ \"y\"), #none])"
          "println(options)"
          "println(array.fromList([1.5, 2.5]))"
          "def bytes: Array(U8) = array.fromList([1, 255])"
          "println(array.at(bytes, 1))" ]
    |> runSource
    |> assertRan "#some(x = 3, y = 4)\n[#some(xy), #none]\n[1.5, 2.5]\n#some(255)\n"

[<Fact>]
let ``Run - dbg writes an array after its type's name`` () =
    printing [ "def xs = dbg(array.fromList([\"a\", \"b\"]))"; "println(array.length(xs))" ]
    |> runSource
    |> assertRan "dbg: Array[\"a\", \"b\"]\n2\n"
