module TranspilerWasmTests.Features.Sets.Run

open Xunit
open TranspilerWasmTestHelpers

/// A program that runs `lines` in its entry point, after the definitions in `definitions`. `show` writes a bool as `T`
/// or `F`, and `ints` writes a set of ints in order.
let private program (definitions: string list) (lines: string list) =
    [ "import nanyx/console"
      ""
      "def show = { b -> if b then \"T\" else \"F\" }"
      "def ints = { (s: set(int)) -> set.toList(s) \\list.map(int.toString) \\list.fold(\"\", { acc, n -> acc ++ n }) }"
      yield! definitions
      ""
      "@entry"
      "def main = {"
      yield! lines |> List.map (fun line -> "  " + line)
      "}" ]
    |> String.concat "\n"

[<Fact>]
let ``Run - a set holds each element once, in the order they were first added`` () =
    program
        []
        [ "def s = set.fromList([3, 1, 3, 2, 1])"
          "println(ints(s))"
          "println(\"{set.count(s)}\")"
          "println(ints(s \\insert(4) \\insert(1)))"
          "println(ints(set.remove(s, 1)))"
          "println(\"{set.count(set.empty \\insert(5))}\")" ]
    |> runSource
    |> assertRan "312\n3\n3124\n32\n1\n"

[<Fact>]
let ``Run - contains says whether a set holds an element`` () =
    program
        []
        [ "def s = set.fromList([\"tea\", \"cake\"])"
          "println(show(set.contains(s, \"te\" ++ \"a\")))"
          "println(show(s \\contains(\"pie\")))" ]
    |> runSource
    |> assertRan "T\nF\n"

[<Fact>]
let ``Run - union, intersection and difference`` () =
    program
        []
        [ "def a = set.fromList([1, 2, 3])"
          "def b = set.fromList([2, 3, 4])"
          "println(ints(set.union(a, b)))"
          "println(ints(set.intersection(a, b)))"
          "println(ints(set.difference(a, b)))" ]
    |> runSource
    |> assertRan "1234\n23\n1\n"

[<Fact>]
let ``Run - records, tuples and tags can be elements, compared by their structure`` () =
    program
        [ "type Point = (x: int, y: int)"
          "type Colour = #red | #green | #blue"
          "def add: set(Colour), Colour -> set(Colour) = { s, c -> s \\insert(c) }" ]
        [ "def points = set.fromList([Point(x = 1, y = 2), Point(x = 1, y = 2), Point(x = 3, y = 4)])"
          "println(\"{set.count(points)} {show(set.contains(points, Point(x = 3, y = 4)))}\")"
          "def pairs = set.fromList([(1, \"a\"), (1, \"a\"), (2, \"a\")])"
          "println(\"{set.count(pairs)}\")"
          "def colours = add(add(add(set.empty, #red), #green), #red)"
          "println(\"{set.count(colours)} {show(set.contains(colours, #blue))}\")" ]
    |> runSource
    |> assertRan "2 T\n2\n2 F\n"

[<Fact>]
let ``Run - a for loop walks a set's elements`` () =
    program
        []
        [ "for set.fromList([2, 1, 2]) | n -> println(\"{n}\")" ]
    |> runSource
    |> assertRan "2\n1\n"

[<Fact>]
let ``Run - isEmpty says whether a set has no elements`` () =
    program
        []
        [ """def none: set(int) = set.empty"""
          """println(show(set.isEmpty(none)))"""
          """println(show(set.fromList([1]) \set.isEmpty))"""
          """println(show(set.isEmpty(set.remove(set.fromList([1]), 1))))""" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - fold walks a set's elements in order`` () =
    program
        []
        [ """def s = set.fromList([3, 1, 2])"""
          """println("{s \set.fold(0, { total, n -> total + n })}")"""
          """println(s \set.fold("", { acc, n -> acc ++ "{n}" }))""" ]
    |> runSource
    |> assertRan "6\n312\n"

[<Fact>]
let ``Run - map gives the set of what a function makes of each element`` () =
    program
        []
        [ """def s = set.fromList([1, 2, 3])"""
          """println("{s \set.map { % 2 } \set.count}")"""
          """println(ints(s \set.map { % 2 }))"""
          """println(ints(s \set.map { * 10 }))""" ]
    |> runSource
    |> assertRan "2\n10\n102030\n"

[<Fact>]
let ``Run - filter keeps the elements that pass the test`` () =
    program
        []
        [ """def s = set.fromList([1, 2, 3, 4])"""
          """println(ints(s \set.filter { n -> n % 2 == 0 }))"""
          """println(show(s \set.filter({ n -> n > 9 }) \set.isEmpty))""" ]
    |> runSource
    |> assertRan "24\nT\n"

[<Fact>]
let ``Run - isSubset says whether another set holds every element`` () =
    program
        []
        [ """println(show(set.isSubset(set.fromList([1]), set.fromList([1, 2]))))"""
          """println(show(set.isSubset(set.fromList([1, 3]), set.fromList([1, 2]))))"""
          """println(show(set.isSubset(set.empty, set.fromList([1]))))"""
          """println(show(set.fromList([2, 1]) \set.isSubset(set.fromList([1, 2]))))""" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\n"
