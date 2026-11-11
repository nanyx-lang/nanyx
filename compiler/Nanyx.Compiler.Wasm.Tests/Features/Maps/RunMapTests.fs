module TranspilerWasmTests.Features.Maps.Run

open Xunit
open TranspilerWasmTestHelpers

/// A program that runs `lines` in its entry point, after the definitions in `definitions`. `found` prints what a
/// lookup of an int found.
let private program (definitions: string list) (lines: string list) =
    [ "import nanyx/console"
      ""
      "def found = {"
      "  | #some(n) -> println(\"{n}\")"
      "  | #notFound -> println(\"not found\")"
      "}"
      yield! definitions
      ""
      "@entry"
      "def main = {"
      yield! lines |> List.map (fun line -> "  " + line)
      "}" ]
    |> String.concat "\n"

[<Fact>]
let ``Run - calling a map looks a key up`` () =
    program
        []
        [ "def m = [\"a\" => 1, \"b\" => 2]"
          "found(m(\"a\"))"
          "found(m(\"b\"))"
          "found(m(\"c\"))"
          "def counts = [1 => 10, 2 => 20]"
          "found(counts([\"x\", \"y\"] \\list.length))" ]
    |> runSource
    |> assertRan "1\n2\nnot found\n20\n"

[<Fact>]
let ``Run - a map literal can be written one entry per line`` () =
    program
        []
        [ "def m = ["
          "  1 => \"one\""
          "  2 => \"two\""
          "]"
          "println(map.get(m, 2) \\defaultValueOption(\"none\"))" ]
    |> runSource
    |> assertRan "two\n"

[<Fact>]
let ``Run - a key written twice in a literal holds the value written last`` () =
    program
        []
        [ "def m = [\"a\" => 1, \"b\" => 2, \"a\" => 3]"
          "found(m(\"a\"))"
          "println(\"{map.count(m)}\")" ]
    |> runSource
    |> assertRan "3\n2\n"

[<Fact>]
let ``Run - insert adds a key, or replaces its value, and leaves the map it was given as it was`` () =
    program
        [ "def keys = { m -> map.toList(m) \\list.map({ entry -> entry.0 }) \\list.fold(\"\", { acc, k -> acc ++ k }) }" ]
        [ "def m = [\"a\" => 1, \"b\" => 2]"
          "def added = m \\insert(\"c\", 3)"
          "def replaced = added \\map.insert(\"a\", 10)"
          "found(added(\"c\"))"
          "found(replaced(\"a\"))"
          "found(m(\"c\"))"
          "println(keys(replaced))"
          "println(\"{map.count(replaced)}\")" ]
    |> runSource
    |> assertRan "3\n10\nnot found\nabc\n3\n"

[<Fact>]
let ``Run - remove takes a key out, and containsKey says whether a map has one`` () =
    program
        [ "def show = { b -> if b then \"T\" else \"F\" }" ]
        [ "def m = map.fromList([(1, 10), (2, 20), (3, 30)])"
          "def removed = map.remove(m, 2)"
          "println(show(map.containsKey(m, 2)))"
          "println(show(map.containsKey(removed, 2)))"
          "found(removed(3))"
          "println(\"{map.count(map.remove(removed, 7))}\")" ]
    |> runSource
    |> assertRan "T\nF\n30\n2\n"

[<Fact>]
let ``Run - records and tags can be keys, compared by their structure`` () =
    program
        [ "type Point = (x: int, y: int)" ]
        [ "def byPoint = [Point(x = 1, y = 2) => 12, Point(x = 3, y = 4) => 34]"
          "found(byPoint(Point(x = 3, y = 4)))"
          "found(byPoint(Point(x = 4, y = 3)))"
          "def byTag = [#rgb(0, 0, 0) => 1, #rgb(1, 2, 3) => 2]"
          "found(byTag(#rgb(1, 2, 3)))"
          "found(byTag(#rgb(3, 2, 1)))" ]
    |> runSource
    |> assertRan "34\nnot found\n2\nnot found\n"

[<Fact>]
let ``Run - a map in a record's field is looked up by calling the field`` () =
    program
        [ "type Shop = (prices: map(string, int))" ]
        [ "def shop = Shop(prices = [\"tea\" => 3, \"cake\" => 4])"
          "found(shop.prices(\"cake\"))" ]
    |> runSource
    |> assertRan "4\n"

[<Fact>]
let ``Run - a map is passed to and given back from functions`` () =
    program
        [ "def stock: map(string, int), string -> int = { m, name ->"
          "  match m(name)"
          "    | #some(n) -> n"
          "    | #notFound -> 0"
          "}"
          "def restock: map(string, int), string -> map(string, int) = { m, name -> m \\insert(name, stock(m, name) + 1) }" ]
        [ "def m = restock(restock([\"tea\" => 1], \"tea\"), \"cake\")"
          "println(\"{stock(m, \"tea\")} {stock(m, \"cake\")} {stock(m, \"pie\")}\")" ]
    |> runSource
    |> assertRan "2 1 0\n"

[<Fact>]
let ``Run - List.groupBy groups elements by a key, in order`` () =
    program
        [ "def show = { xs -> xs \\list.map(int.toString) \\list.fold(\"\", { acc, s -> acc ++ s }) }" ]
        [ "def groups = [1, 2, 3, 4, 5] \\list.groupBy { x -> x % 2 }"
          "println(show(groups(1) \\defaultValueOption([])))"
          "println(show(groups(0) \\defaultValueOption([])))"
          "println(show(map.toList(groups) \\list.map({ group -> group.0 })))"
          "println(\"{map.count(list.groupBy([], { (x: int) -> x }))}\")" ]
    |> runSource
    |> assertRan "135\n24\n10\n0\n"

[<Fact>]
let ``Run - a for loop walks a map's entries, in the order their keys were added`` () =
    program
        []
        [ "def dict = [\"one\" => \"uno\", \"two\" => \"dos\"] \\insert(\"one\", \"un\")"
          "for dict | key, value -> println(\"{key} => {value}\")"
          "for dict, 1.. | entry, i -> println(\"{i}: {entry.1}\")" ]
    |> runSource
    |> assertRan "one => un\ntwo => dos\n1: un\n2: dos\n"

[<Fact>]
let ``Run - a nominal type with its own == and hash can be a key, compared by its own ==`` () =
    program
        [ "type $Code = string"
          "def $Code.`==`: $Code, $Code -> bool = { a, b -> true }"
          "def $Code.hash: $Code -> int = { c -> 0 }" ]
        [ "def m = [$Code(\"ab\") => 1] \\insert($Code(\"xy\"), 2)"
          "println(\"{map.count(m)}\")"
          "found(m($Code(\"zz\")))" ]
    |> runSource
    |> assertRan "1\n2\n"

[<Fact>]
let ``Run - tags of different names can be keys of a map whose key type is written`` () =
    program
        [ "type Colour = #red | #green | #blue" ]
        [ "def lights: map(Colour, int) = map.empty \\insert(#red, 1) \\insert(#green, 2)"
          "found(lights(#green))"
          "found(lights(#blue))" ]
    |> runSource
    |> assertRan "2\nnot found\n"

[<Fact>]
let ``Run - keys and values give a map's keys and values in order`` () =
    program
        [ "def joined = { xs -> xs \\list.fold(\"\", { acc, x -> acc ++ x }) }" ]
        [ "def m = [\"b\" => \"2\", \"a\" => \"1\"] \\insert(\"c\", \"3\")"
          "println(joined(map.keys(m)))"
          "println(joined(map.values(m)))" ]
    |> runSource
    |> assertRan "bac\n213\n"

[<Fact>]
let ``Run - isEmpty says whether a map has no keys`` () =
    program
        [ "def show = { b -> if b then \"T\" else \"F\" }" ]
        [ "def none: map(string, int) = Map.empty"
          "println(show(Map.isEmpty(none)))"
          "println(show([\"a\" => 1] \\Map.isEmpty))"
          "println(show(Map.isEmpty(Map.remove([\"a\" => 1], \"a\"))))" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - fold walks a map's keys and values in order`` () =
    program
        []
        [ "def m = [\"a\" => 1, \"b\" => 2]"
          "println(\"{m \\Map.fold(0, { total, key, value -> total + value })}\")"
          "println(m \\Map.fold(\"\", { acc, key, value -> acc ++ key ++ \"{value}\" }))" ]
    |> runSource
    |> assertRan "3\na1b2\n"

[<Fact>]
let ``Run - map changes a map's values and keeps its keys`` () =
    program
        []
        [ "def m = [\"a\" => 1, \"b\" => 2] \\Map.map { * 10 }"
          "found(m \\Map.get(\"b\"))"
          "println(Map.keys(m) \\List.fold(\"\", { acc, k -> acc ++ k }))"
          "def named = m \\Map.map { n -> \"#{n}\" }"
          "println(Map.get(named, \"a\") \\defaultValueOption(\"none\"))" ]
    |> runSource
    |> assertRan "20\nab\n#10\n"

[<Fact>]
let ``Run - filter keeps the entries that pass the test`` () =
    program
        []
        [ "def m = [\"a\" => 1, \"b\" => 2, \"c\" => 3]"
          "println(m \\Map.filter { key, value -> value > 1 } \\Map.keys \\List.fold(\"\", { acc, k -> acc ++ k }))"
          "println(\"{m \\Map.filter({ key, value -> key == \"a\" }) \\Map.count}\")" ]
    |> runSource
    |> assertRan "bc\n1\n"

[<Fact>]
let ``Run - update adds a missing key and changes a present one`` () =
    program
        [ "def bump = { | #some(n) -> n + 1 | #notFound -> 1 }" ]
        [ "def counts = [\"tea\" => 2]"
          "def more = counts \\Map.update(\"tea\", bump) \\Map.update(\"cake\", bump)"
          "found(more(\"tea\"))"
          "found(more(\"cake\"))"
          "found(counts(\"tea\"))"
          "println(Map.keys(more) \\List.fold(\"\", { acc, k -> acc ++ k }))" ]
    |> runSource
    |> assertRan "3\n1\n2\nteacake\n"
