module TranspilerWasmTests.Features.Sets.Run

open Xunit
open TranspilerWasmTestHelpers

/// A program that runs `lines` in its entry point, after the definitions in `definitions`. `show` writes a bool as `T`
/// or `F`, and `ints` writes a set of ints in order.
let private program (definitions: string list) (lines: string list) =
    [ "import nanyx/console"
      ""
      "def show = { b -> if b then \"T\" else \"F\" }"
      "def ints = { (s: set(int)) -> Set.toList(s) \\List.map(Int.toString) \\List.fold(\"\", { acc, n -> acc ++ n }) }"
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
        [ "def s = Set.fromList([3, 1, 3, 2, 1])"
          "println(ints(s))"
          "println(\"{Set.count(s)}\")"
          "println(ints(s \\insert(4) \\insert(1)))"
          "println(ints(Set.remove(s, 1)))"
          "println(\"{Set.count(Set.empty \\insert(5))}\")" ]
    |> runSource
    |> assertRan "312\n3\n3124\n32\n1\n"

[<Fact>]
let ``Run - contains says whether a set holds an element`` () =
    program
        []
        [ "def s = Set.fromList([\"tea\", \"cake\"])"
          "println(show(Set.contains(s, \"te\" ++ \"a\")))"
          "println(show(s \\contains(\"pie\")))" ]
    |> runSource
    |> assertRan "T\nF\n"

[<Fact>]
let ``Run - union, intersection and difference`` () =
    program
        []
        [ "def a = Set.fromList([1, 2, 3])"
          "def b = Set.fromList([2, 3, 4])"
          "println(ints(Set.union(a, b)))"
          "println(ints(Set.intersection(a, b)))"
          "println(ints(Set.difference(a, b)))" ]
    |> runSource
    |> assertRan "1234\n23\n1\n"

[<Fact>]
let ``Run - records, tuples and tags can be elements, compared by their structure`` () =
    program
        [ "type Point = (x: int, y: int)"
          "type Colour = #red | #green | #blue"
          "def add: set(Colour), Colour -> set(Colour) = { s, c -> s \\insert(c) }" ]
        [ "def points = Set.fromList([Point(x = 1, y = 2), Point(x = 1, y = 2), Point(x = 3, y = 4)])"
          "println(\"{Set.count(points)} {show(Set.contains(points, Point(x = 3, y = 4)))}\")"
          "def pairs = Set.fromList([(1, \"a\"), (1, \"a\"), (2, \"a\")])"
          "println(\"{Set.count(pairs)}\")"
          "def colours = add(add(add(Set.empty, #red), #green), #red)"
          "println(\"{Set.count(colours)} {show(Set.contains(colours, #blue))}\")" ]
    |> runSource
    |> assertRan "2 T\n2\n2 F\n"

[<Fact>]
let ``Run - a for loop walks a set's elements`` () =
    program
        []
        [ "for Set.fromList([2, 1, 2]) | n -> println(\"{n}\")" ]
    |> runSource
    |> assertRan "2\n1\n"
