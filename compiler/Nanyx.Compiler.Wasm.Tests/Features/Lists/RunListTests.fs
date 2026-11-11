module TranspilerWasmTests.Features.Lists.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - list literals and patterns`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: list(int) -> string = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> \"empty\"\n" +
        "  | [_x] -> \"one\"\n" +
        "  | [1, _y] -> \"one then another\"\n" +
        "  | [_x, ..._rest] -> \"many\"\n" +
        "}\n" +
        "def sum: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> 0\n" +
        "  | [x, ...rest] -> x + sum(rest)\n" +
        "}\n" +
        "println(describe([]))\n" +
        "println(describe([7]))\n" +
        "println(describe([1, 2]))\n" +
        "println(describe([2, 2, 2]))\n" +
        "dbg(sum([1, 2, 3, 4]))\n"
    runSource source |> assertRan "empty\none\none then another\nmany\ndbg: 10\n"

[<Fact>]
let ``Run - an unnamed rest matches a longer list`` () =
    // `[x, ...]` binds the head and ignores the rest, whatever its length.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def first: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "  | [x, ...] -> x\n" +
        "  | [] -> 0\n" +
        "}\n" +
        "dbg(first([5, 6, 7]))\n" +
        "dbg(first([8]))\n" +
        "dbg(first([]))\n"
    runSource source |> assertRan "dbg: 5\ndbg: 8\ndbg: 0\n"

[<Fact>]
let ``Run - list spread in expressions`` () =
    // Regression: `[1, ...rest]` worked as a pattern but not as an expression (#44).
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def sum: list(int) -> int = {\n" +
        "  | [] -> 0\n" +
        "  | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def xs = [1, ...[2, 3]]\n" +
        "  println(\"{sum(xs)}\")\n" +
        "  def names = [\"b\"]\n" +
        "  println(string.join([\"a\", ...names, \"c\", ...[], ...[\"d\"]], \"-\"))\n" +
        "}\n"
    runSource source |> assertRan "6\na-b-c-d\n"

[<Fact>]
let ``Run - ++ joins lists`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def front = [\"a\", \"b\"]\n" +
        "println(string.join(front ++ [\"c\"] ++ [], \"-\"))\n" +
        "println(string.join([] ++ front, \"-\"))\n" +
        "dbg(string.length(\"ab\" ++ \"cd\" ++ \"e\"))\n"
    runSource source |> assertRan "a-b-c\na-b\ndbg: 5\n"

[<Fact>]
let ``Run - the prelude's List functions`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: list(int) -> string = {\n" +
        "  | [] -> \"[]\"\n" +
        "  | [y, ...rest] -> \"{y} \" ++ show(rest)\n" +
        "}\n" +
        "def xs = [3, 1, 2]\n" +
        "println(show(xs \\ map({ x -> x * 2 })))\n" +
        "println(show(xs \\ filter({ x -> x > 1 })))\n" +
        "println(show(list.reverse(xs)))\n" +
        "println(show(list.flatten([[1, 2], [3], []])))\n" +
        "println(show(list.take(xs, 2)) ++ \" | \" ++ show(list.skip(xs, 2)) ++ \" | \" ++ show(list.append(xs, 9)))\n" +
        "def total: int = list.fold(xs, 0, { acc, x -> acc + x })\n" +
        "def back: int = list.foldBack([1, 2, 3], 0, { acc, x -> acc - x })\n" +
        "def count: int = list.length(xs)\n" +
        "def pairs: int = list.length(list.zip(xs, [\"a\", \"b\"]))\n" +
        "println(\"{total} {back} {count} {pairs}\")\n" +
        "println(if list.all(xs, { x -> x > 0 }) then \"all\" else \"not all\")\n" +
        "println(if list.any(xs, { x -> x > 5 }) then \"any\" else \"none\")\n" +
        "def found = xs \\ find({ x -> x < 3 })\n" +
        "def empty: list(int) = []\n" +
        "def first = list.head(empty)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match found\n" +
        "    | #some(n) -> println(\"found {n}\")\n" +
        "    | #notFound -> println(\"not found\")\n" +
        "  match first\n" +
        "    | #some(n) -> println(\"head {n}\")\n" +
        "    | #emptyList -> println(\"empty\")\n" +
        "}\n"
    runSource source
    |> assertRan (
        "6 2 4 []\n3 2 []\n2 1 3 []\n1 2 3 []\n3 1 [] | 2 [] | 3 1 2 9 []\n" +
        "6 -6 3 2\nall\nnone\nfound 1\nempty\n")

/// Prints a list of ints, for the tests of the prelude's List functions below.
let private showInts =
    "import nanyx/console\n" +
    "import nanyx/text\n" +
    "\n" +
    "def show: list(int) -> string = {\n" +
    "  | [] -> \"[]\"\n" +
    "  | [y, ...rest] -> \"{y} \" ++ show(rest)\n" +
    "}\n"

[<Fact>]
let ``Run - List.distinct keeps the first of each value`` () =
    // The tags are bound to annotated names first: a list literal of different tags doesn't type-check yet (#481).
    let source =
        showInts +
        "type Color = #red | #green\n" +
        "def red: Color = #red\n" +
        "def green: Color = #green\n" +
        "println(show(list.distinct([1, 2, 2, 3, 1])))\n" +
        "println(string.join(list.distinct([\"b\", \"a\", \"b\"]), \",\"))\n" +
        "dbg(list.length(list.distinct([red, green, red, red])))\n" +
        "dbg(list.length(list.distinct([true, true])))\n" +
        "println(show(list.distinct([])))\n"
    runSource source |> assertRan "1 2 3 []\nb,a\ndbg: 2\ndbg: 1\n[]\n"

[<Fact>]
let ``Run - list.distinct compares records, tuples and lists by their structure`` () =
    // #191: `distinct` is `[Eq(a)] list(a) -> list(a)`; it took numbers, bools, strings and tags only.
    let source =
        showInts
        + """type Person = (name: string, age: int)
def people: list(Person) = [
  Person(name = "Ann", age = 40), Person(name = "Bob", age = 30), Person(name = "Ann", age = 40)]
println(string.join(list.map(list.distinct(people), { p -> p.name }), ","))
println(string.join(list.map(list.distinct([(1, "a"), (2, "a"), (1, "a")]), { p -> "{p.0}{p.1}" }), ","))
println("{list.length(list.distinct([[1, 2], [2, 1], [1, 2], []]))}")
"""
    runSource source |> assertRan "Ann,Bob\n1a,2a\n3\n"

[<Fact>]
let ``Run - List.sortBy sorts by an int key and keeps equal keys in order`` () =
    let source =
        showInts +
        "type Person = (name: string, age: int)\n" +
        "def people: list(Person) = [\n" +
        "  Person(name = \"Ann\", age = 40), Person(name = \"Bob\", age = 30),\n" +
        "  Person(name = \"Cy\", age = 40), Person(name = \"Di\", age = 20)]\n" +
        "println(show(list.sortBy([5, 3, 9, 1, 3, 0, 7], { x -> x })))\n" +
        "println(string.join(list.sortBy([\"bbb\", \"a\", \"cc\"], { .length }), \",\"))\n" +
        "println(string.join(list.map(people \\ sortBy({ .age }), { p -> p.name }), \",\"))\n" +
        "println(show(list.sortBy([], { x -> x })))\n"
    runSource source |> assertRan "0 1 3 3 5 7 9 []\na,cc,bbb\nDi,Bob,Ann,Cy\n[]\n"

[<Fact>]
let ``Run - List.unfold builds a list from a state until #done`` () =
    // The docs' `{ current, next -> ... }` takes the tuple apart in the parameters, which the backend can't call yet
    // (#441), so this one takes it apart in a tuple pattern.
    let source =
        showInts +
        "def fibonacci = list.unfold((0, 1)) { (current, next) ->\n" +
        "  if current > 100 then #done\n" +
        "  else #some(current, (next, current + next))\n" +
        "}\n" +
        "println(show(fibonacci))\n" +
        "println(show(list.unfold(3, { n -> if n == 0 then #done else #some(n, n - 1) })))\n" +
        "println(show(list.unfold(0, { n -> #done })))\n"
    runSource source |> assertRan "0 1 1 2 3 5 8 13 21 34 55 89 []\n3 2 1 []\n[]\n"

[<Fact>]
let ``Run - List search and slicing helpers`` () =
    let source =
        showInts +
        "def xs = [3, 1, 4, 1, 5]\n" +
        "def empty: list(int) = []\n" +
        "def describe: #some(int) | #notFound -> string = {\n" +
        "  | #some(n) -> \"at {n}\"\n" +
        "  | #notFound -> \"not found\"\n" +
        "}\n" +
        "def describeLast: #some(int) | #emptyList -> string = {\n" +
        "  | #some(n) -> \"last {n}\"\n" +
        "  | #emptyList -> \"no last\"\n" +
        "}\n" +
        "println(if list.contains(xs, 4) then \"has 4\" else \"no 4\")\n" +
        "println(if xs \\ contains(9) then \"has 9\" else \"no 9\")\n" +
        "println(if list.contains([\"a\", \"b\"], \"b\") then \"has b\" else \"no b\")\n" +
        "println(describe(list.indexOf(xs, 1)))\n" +
        "println(describe(list.indexOf(xs, 9)))\n" +
        "dbg(list.count(xs, { x -> x > 1 }))\n" +
        "dbg(list.count(empty, { x -> x > 1 }))\n" +
        "println(if list.isEmpty(empty) then \"empty\" else \"not empty\")\n" +
        "println(if xs \\ isEmpty then \"empty\" else \"not empty\")\n" +
        "println(describeLast(list.last(xs)))\n" +
        "println(describeLast(list.last(empty)))\n" +
        "println(show(list.dropLast(xs)))\n" +
        "println(show(list.dropLast(empty)))\n" +
        "println(show(list.takeWhile(xs, { x -> x < 4 })))\n" +
        "println(show(list.takeWhile(xs, { x -> x > 9 })))\n" +
        "println(show(list.mapIndexed(xs, { i, x -> i * 10 + x })))\n" +
        "println(show(list.flatMap([1, 2, 3], { x -> list.take([x, x, x], x) })))\n" +
        "println(show(list.flatMap(empty, { x -> [x] })))\n"
    runSource source
    |> assertRan (
        "has 4\nno 9\nhas b\nat 1\nnot found\ndbg: 3\ndbg: 0\nempty\nnot empty\nlast 5\nno last\n" +
        "3 1 4 1 []\n[]\n3 1 []\n[]\n3 11 24 31 45 []\n1 2 2 3 3 3 []\n[]\n")

[<Fact>]
let ``Run - List.skipWhile, partition, unzip and get`` () =
    let source =
        showInts +
        "def xs = [1, 2, 3, 4]\n" +
        "def empty: list(int) = []\n" +
        "def describe: #some(int) | #outOfRange -> string = {\n" +
        "  | #some(n) -> \"some {n}\"\n" +
        "  | #outOfRange -> \"out of range\"\n" +
        "}\n" +
        "println(show(xs \\ skipWhile { < 3 }))\n" +
        "println(show(list.skipWhile([1, 5, 1], { x -> x < 3 })))\n" +
        "println(show(list.skipWhile(xs, { x -> x < 9 })))\n" +
        "println(show(list.takeWhile(xs, { x -> x < 3 }) ++ list.skipWhile(xs, { x -> x < 3 })))\n" +
        "def (small, big) = xs \\ partition { < 3 }\n" +
        "println(show(small) ++ show(big))\n" +
        "def (odd, even) = list.partition([5, 2, 3, 4, 1], { x -> x % 2 == 1 })\n" +
        "println(show(odd) ++ show(even))\n" +
        "def (none, all) = list.partition(empty, { x -> x > 0 })\n" +
        "println(show(none) ++ show(all))\n" +
        "def (numbers, names) = list.unzip([(1, \"a\"), (2, \"b\")])\n" +
        "println(show(numbers) ++ string.join(names, \",\"))\n" +
        "def (firsts, seconds) = list.unzip(list.zip(xs, [5, 6, 7, 8]))\n" +
        "println(show(firsts) ++ show(seconds))\n" +
        "println(describe([10, 20, 30] \\ list.get(1)))\n" +
        "println(describe([10, 20, 30] \\ get(0)))\n" +
        "println(describe(list.get([10, 20, 30], 2)))\n" +
        "println(describe(list.get([10, 20, 30], -1)))\n" +
        "println(describe(list.get([10, 20, 30], 3)))\n" +
        "println(describe(list.get(empty, 0)))\n"
    runSource source
    |> assertRan (
        "3 4 []\n5 1 []\n[]\n1 2 3 4 []\n1 2 []3 4 []\n5 3 1 []2 4 []\n[][]\n1 2 []a,b\n1 2 3 4 []5 6 7 8 []\n" +
        "some 20\nsome 10\nsome 30\nout of range\nout of range\nout of range\n")
