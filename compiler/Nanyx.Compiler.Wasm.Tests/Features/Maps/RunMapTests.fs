module TranspilerWasmTests.Features.Maps.Run

open Xunit
open TranspilerWasmTestHelpers

/// A program that runs `lines` in its entry point, after the definitions in `definitions`. `found` prints what a
/// lookup of an int found.
let private program (definitions: string list) (lines: string list) =
    [ "def found = {"
      """  | #some(n) -> println("{n}")"""
      """  | #notFound -> println("not found")"""
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
        [ """def m = ["a" => 1, "b" => 2]"""
          """found(m("a"))"""
          """found(m("b"))"""
          """found(m("c"))"""
          "def counts = [1 => 10, 2 => 20]"
          """found(counts(["x", "y"] \list.length))""" ]
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
          """println(map.get(m, 2) \someOr("none"))""" ]
    |> runSource
    |> assertRan "two\n"

[<Fact>]
let ``Run - a key written twice in a literal holds the value written last`` () =
    program
        []
        [ """def m = ["a" => 1, "b" => 2, "a" => 3]"""
          """found(m("a"))"""
          """println("{map.length(m)}")""" ]
    |> runSource
    |> assertRan "3\n2\n"

[<Fact>]
let ``Run - insert adds a key, or replaces its value, and leaves the map it was given as it was`` () =
    program
        [ """def keys = { m -> map.toList(m) \list.map({ entry -> entry.0 }) \list.fold("", { acc, k -> acc ++ k }) }""" ]
        [ """def m = ["a" => 1, "b" => 2]"""
          """def added = m \insert("c", 3)"""
          """def replaced = added \map.insert("a", 10)"""
          """found(added("c"))"""
          """found(replaced("a"))"""
          """found(m("c"))"""
          "println(keys(replaced))"
          """println("{map.length(replaced)}")""" ]
    |> runSource
    |> assertRan "3\n10\nnot found\nbac\n3\n"

[<Fact>]
let ``Run - remove takes a key out, and containsKey says whether a map has one`` () =
    program
        [ """def show = { b -> if b then "T" else "F" }""" ]
        [ "def m = map.fromList([(1, 10), (2, 20), (3, 30)])"
          "def removed = map.remove(m, 2)"
          "println(show(map.containsKey(m, 2)))"
          "println(show(map.containsKey(removed, 2)))"
          "found(removed(3))"
          """println("{map.length(map.remove(removed, 7))}")""" ]
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
        [ """def shop = Shop(prices = ["tea" => 3, "cake" => 4])"""
          """found(shop.prices("cake"))""" ]
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
        [ """def m = restock(restock(["tea" => 1], "tea"), "cake")"""
          """println("{stock(m, "tea")} {stock(m, "cake")} {stock(m, "pie")}")""" ]
    |> runSource
    |> assertRan "2 1 0\n"

[<Fact>]
let ``Run - list.groupBy takes a key of any type with an Eq and a Hash`` () =
    // #191: a string key, and a tuple key.
    program
        [ """type Person = (name: string, city: string, age: int)"""
          """def names: list(Person) -> string = { ps -> list.fold(ps, "", { acc, p -> acc ++ p.name ++ "," }) }""" ]
        [ """def people: list(Person) = [Person(name = "Ann", city = "Leeds", age = 40), Person(name = "Bob", city = "York", age = 30), Person(name = "Cy", city = "Leeds", age = 30)]"""
          """def byCity = people \groupBy { p -> p.city }"""
          """println(list.fold(map.keys(byCity), "", { acc, k -> acc ++ k ++ " " }))"""
          """println(names(byCity("Leeds") \someOr([])))"""
          """def byBoth = people \groupBy { p -> (p.city, p.age) }"""
          """println("{map.length(byBoth)} {names(byBoth(("Leeds", 30)) \someOr([]))}")""" ]
    |> runSource
    |> assertRan "York Leeds \nAnn,Cy,\n3 Cy,\n"

[<Fact>]
let ``Run - List.groupBy groups elements by a key, in order`` () =
    program
        [ """def show = { xs -> xs \list.map(int.toString) \list.fold("", { acc, s -> acc ++ s }) }""" ]
        [ "def groups = [1, 2, 3, 4, 5] \\list.groupBy { x -> x % 2 }"
          "println(show(groups(1) \\someOr([])))"
          "println(show(groups(0) \\someOr([])))"
          "println(show(map.toList(groups) \\list.map({ group -> group.0 })))"
          """println("{map.length(list.groupBy([], { (x: int) -> x }))}")""" ]
    |> runSource
    |> assertRan "135\n24\n01\n0\n"

[<Fact>]
let ``Run - a for loop walks a map's entries, in the order of map.toList`` () =
    program
        []
        [ """def dict = ["one" => "uno", "two" => "dos"] \insert("one", "un")"""
          """for dict | key, value -> println("{key} => {value}")"""
          """for dict, 1.. | entry, i -> println("{i}: {entry.1}")""" ]
    |> runSource
    |> assertRan "two => dos\none => un\n1: dos\n2: un\n"

[<Fact>]
let ``Run - a nominal type with its own == and hash can be a key, compared by its own ==`` () =
    program
        [ "type Code = protected string"
          "def Code.`==`: Code, Code -> bool = { a, b -> true }"
          "def Code.hash: Code -> int = { c -> 0 }" ]
        [ """def m = [Code("ab") => 1] \insert(Code("xy"), 2)"""
          """println("{map.length(m)}")"""
          """found(m(Code("zz")))""" ]
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
        [ """def joined = { xs -> xs \list.fold("", { acc, x -> acc ++ x }) }""" ]
        [ """def m = ["b" => "2", "a" => "1"] \insert("c", "3")"""
          "println(joined(map.keys(m)))"
          "println(joined(map.values(m)))" ]
    |> runSource
    |> assertRan "bac\n213\n"

[<Fact>]
let ``Run - isEmpty says whether a map has no keys`` () =
    program
        [ """def show = { b -> if b then "T" else "F" }""" ]
        [ """def none: map(string, int) = map.empty"""
          """println(show(map.isEmpty(none)))"""
          """println(show(["a" => 1] \map.isEmpty))"""
          """println(show(map.isEmpty(map.remove(["a" => 1], "a"))))""" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - fold walks a map's keys and values in order`` () =
    program
        []
        [ """def m = ["a" => 1, "b" => 2]"""
          """println("{m \map.fold(0, { total, key, value -> total + value })}")"""
          """println(m \map.fold("", { acc, key, value -> acc ++ key ++ "{value}" }))""" ]
    |> runSource
    |> assertRan "3\nb2a1\n"

[<Fact>]
let ``Run - map changes a map's values and keeps its keys`` () =
    program
        []
        [ """def m = ["a" => 1, "b" => 2] \map.map { * 10 }"""
          """found(m \map.get("b"))"""
          """println(map.keys(m) \list.fold("", { acc, k -> acc ++ k }))"""
          """def named = m \map.map { n -> "#{n}" }"""
          """println(map.get(named, "a") \someOr("none"))""" ]
    |> runSource
    |> assertRan "20\nba\n#10\n"

[<Fact>]
let ``Run - filter keeps the entries that pass the test`` () =
    program
        []
        [ """def m = ["a" => 1, "b" => 2, "c" => 3]"""
          """println(m \map.filter { key, value -> value > 1 } \map.keys \list.fold("", { acc, k -> acc ++ k }))"""
          """println("{m \map.filter({ key, value -> key == "a" }) \map.length}")""" ]
    |> runSource
    |> assertRan "bc\n1\n"

[<Fact>]
let ``Run - update adds a missing key and changes a present one`` () =
    program
        [ """def bump = { | #some(n) -> n + 1 | #notFound -> 1 }""" ]
        [ """def counts = ["tea" => 2]"""
          """def more = counts \map.update("tea", bump) \map.update("cake", bump)"""
          """found(more("tea"))"""
          """found(more("cake"))"""
          """found(counts("tea"))"""
          """println(map.keys(more) \list.fold("", { acc, k -> acc ++ k }))""" ]
    |> runSource
    |> assertRan "3\n1\n2\nteacake\n"


[<Fact>]
let ``Run - union keeps the first map's keys and takes the second's values`` () =
    program
        [ """def show = { m -> println(m \map.toList \list.fold("", { acc, entry -> acc ++ entry.0 ++ "{entry.1}" })) }""" ]
        [ """def defaults = ["a" => 1, "b" => 2]"""
          """show(defaults \map.union(["c" => 3, "d" => 4]))"""
          """show(defaults \map.union(["b" => 20, "c" => 3]))"""
          """show(defaults \map.union(map.empty))"""
          """show(map.union(map.empty, defaults))"""
          """show(["b" => 2, "a" => 1] \map.union(["c" => 3, "a" => 10]))""" ]
    |> runSource
    |> assertRan "b2a1c3d4\nb20a1c3\nb2a1\nb2a1\nb2a10c3\n"

[<Fact>]
let ``Run - unionWith combines the values of a key both maps have`` () =
    program
        [ """def show = { m -> println(m \map.toList \list.fold("", { acc, entry -> acc ++ entry.0 ++ "{entry.1}" })) }""" ]
        [ """def counts = ["tea" => 2, "cake" => 1]"""
          """show(counts \map.unionWith(["pie" => 4], { a, b -> a + b }))"""
          """show(counts \map.unionWith(["cake" => 5, "pie" => 4], { a, b -> a + b }))"""
          """show(counts \map.unionWith(["tea" => 7], { a, b -> a - b }))"""
          """show(counts \map.unionWith(map.empty, { a, b -> a + b }))"""
          """show(map.unionWith(map.empty, counts, { a, b -> a + b }))""" ]
    |> runSource
    |> assertRan "tea2pie4cake1\ntea2pie4cake6\ntea-5cake1\ntea2cake1\ntea2cake1\n"
[<Fact>]
let ``Run - a map is written as its literal is, each key and value by its own toString`` () =
    program
        [ """def counts = ["tea" => 3, "cake" => 1]"""
          """def none: map(int, int) = map.empty"""
          """def described: [ToString(k), ToString(v)] map(k, v) -> string = { m -> "has {m}" }""" ]
        [ """println(counts)"""
          """println("{counts}")"""
          """println([counts, ["pie" => 4]])"""
          """println((count = 2, prices = [1 => #some(2.5)]))"""
          """println(none)"""
          """println(described([#tea => [1, 2]]))""" ]
    |> runSource
    |> assertRan "[tea => 3, cake => 1]\n[tea => 3, cake => 1]\n[[tea => 3, cake => 1], [pie => 4]]\n(count = 2, prices = [1 => #some(2.5)])\n[=>]\nhas [#tea => [1, 2]]\n"

/// A program whose functions build a map of `size` keys, and build one and then look up, insert and remove 100,000 of
/// its keys, so that the work the lookups, inserts and removes take at a size is the one's less the other's.
let private churnable =
    normalized """def built: int -> map(int, int) = { size -> (0..size - 1) \seq.fold(map.empty, { m, k -> map.insert(m, k, k) }) }

def building: int -> int = { size -> map.length(built(size)) }

def churned: int -> int = { size ->
  def m = built(size)
  def found = (0..99999) \seq.fold(0, { total, i -> total + (m(i % size) \someOr(0)) })
  def changed = (0..99999) \seq.fold(m, { kept, i -> map.insert(kept, i % size, i) })
  def removed = (0..99999) \seq.fold(changed, { kept, i -> map.remove(kept, i % size) })
  if found > 0 then map.length(changed) + map.length(removed) else -1
}
"""

[<Fact>]
let ``Run - a lookup, an insert and a remove take work that grows with the log of the map's size`` () =
    use program = instantiateWasm churnable []
    Assert.Equal(1000, program.Call("churned", 1000))
    let churning (size: int) = float (program.Work("churned", size) - program.Work("building", size))
    let small, large = churning 1000, churning 100000
    // 100 times the keys: a list's linear walk takes some 100 times the work; a trie's few levels more take little.
    Assert.True(large < small * 4.0, $"300,000 operations on 1,000 keys took {small} fuel, on 100,000 keys {large}, {large / small:F1} times")

[<Fact>]
let ``Run - keys whose hashes are all the same are kept in one order however they were added, and found and removed`` () =
    runSource
        """type Key = protected string

def Key.`==`: Key, Key -> bool = { Key(a), Key(b) -> a == b }
def Key.hash: Key -> int = { _ -> 7 }
def Key.toString: Key -> string = { Key(a) -> a }

def a = [Key("x") => 1, Key("y") => 2, Key("z") => 3] \map.remove(Key("y")) \map.insert(Key("w"), 4)
def b = [Key("w") => 4, Key("z") => 3] \map.insert(Key("x"), 1)
println(map.keys(a) \list.map(Key.toString) == map.keys(b) \list.map(Key.toString))
println(a == b)
println(a(Key("z")))
println(a(Key("y")))
def left = a \map.remove(Key("w")) \map.remove(Key("x"))
println(map.toList(left) \list.map { entry -> "{entry.0}={entry.1}" })
println(map.length(left \map.remove(Key("z"))))
"""
    |> assertRan "true\ntrue\n#some(3)\n#notFound\n[z=3]\n0\n"

/// A program whose functions build a map of `n` keys and walk it once or twice with `for`, for counting what a walk
/// allocates (see `StoreWalk`).
let private walking =
    normalized """def built: int -> map(int, int) = { n -> (0..n - 1) \seq.fold(map.empty, { m, k -> map.insert(m, k, k) }) }

def walked: map(int, int) -> int = { m ->
  memory {
    mut sum = 0
    for m | (k, v) -> sum := sum + v
    sum
  }
}

def once: int -> int = { n -> walked(built(n)) }

def twice: int -> int = { n ->
  def m = built(n)
  walked(m) + walked(m)
}
"""

[<Fact>]
let ``Run - a for loop over a map allocates nothing for its entries, however many there are`` () =
    use program = instantiateWasm walking []
    let extraPass (n: int) =
        let one, sum = allocationsOf program "once" n
        let two, doubled = allocationsOf program "twice" n
        Assert.Equal(2 * sum, doubled)
        two - one
    // An extra pass makes the same few blocks, its memory block's, whatever the size: none for an entry or a node.
    let small = extraPass 100
    Assert.Equal(small, extraPass 10000)
    Assert.InRange(small, 0, 2)

[<Fact>]
let ``Run - a for loop over a map walks every entry of a deep trie once, nests, and walks in step with a list`` () =
    program
        [ """def deep = (0..40) \seq.map { i -> (i * 32 * 32 * 32, i) } \seq.toList \map.fromList"""
          """def total: map(int, int) -> int = { m ->
  memory {
    mut sum = 0
    for m | (k, v) -> sum := sum + v
    sum
  }
}""" ]
        [ """println(total(deep))"""
          """def pairs = memory {
    mut count = 0
    for deep | (a, _) ->
      for deep | (b, _) -> count := count + 1
    count
  }"""
          """println(pairs)"""
          """for set.fromList([3, 4]), ["x", "y"] | n, s -> println(n + string.length(s))""" ]
    |> runSource
    |> assertRan "820\n1681\n4\n5\n"
