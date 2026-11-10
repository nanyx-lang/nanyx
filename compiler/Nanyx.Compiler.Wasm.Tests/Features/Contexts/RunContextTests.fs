module TranspilerWasmTests.Features.Contexts.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a function requiring Console gets the runtime's instance`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def greet: [Console] string -> () = { name -> println(\"Hello, \" ++ name) }\n" +
        "\n" +
        "greet(\"Nanyx\")\n"
    runSource source |> assertRan "Hello, Nanyx\n"

[<Fact>]
let ``Run - a use binding overrides the runtime's Console`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def greet: [Console] string -> () = { name -> println(name) }\n" +
        "\n" +
        "def main = {\n" +
        "  use (println = { s -> print(\"[\" ++ s ++ \"]\") }, print = { s -> print(s) })\n" +
        "  greet(\"quiet\")\n" +
        "}\n" +
        "\n" +
        "main()\n" +
        "greet(\"loud\")\n"
    runSource source |> assertRan "[quiet]loud\n"

/// A context with one member, and a function that requires it, for the tests of what a `use` can bind (#88).
let private myConsolePrelude =
    "import nanyx/console\n" +
    "\n" +
    "context MyConsole = (p: string -> ())\n" +
    "\n" +
    "def greet: [MyConsole] string -> () = { name -> p(name) }\n" +
    "\n"

[<Fact>]
let ``Run - a top-level function as a use binding`` () =
    let source =
        myConsolePrelude +
        "@entry\n" +
        "def main = {\n" +
        "  use MyConsole(\n" +
        "    p = println\n" +
        "  )\n" +
        "  greet(\"Nanyx\")\n" +
        "}\n"
    runSource source |> assertRan "Nanyx\n"

[<Fact>]
let ``Run - a use binding lambda that captures a local`` () =
    let source =
        myConsolePrelude +
        "@entry\n" +
        "def main = {\n" +
        "  def prefix = \">> \"\n" +
        "  use MyConsole(\n" +
        "    p = { s -> println(\"{prefix}{s}\") }\n" +
        "  )\n" +
        "  greet(\"captured\")\n" +
        "}\n"
    runSource source |> assertRan ">> captured\n"

[<Fact>]
let ``Run - a parameter or local holding a function as a use binding`` () =
    let source =
        myConsolePrelude +
        "def viaParameter: (string -> ()) -> () = { f ->\n" +
        "  use MyConsole(p = f)\n" +
        "  greet(\"parameter\")\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def shout = { s -> println(s ++ \"!\") }\n" +
        "  use MyConsole(p = shout)\n" +
        "  greet(\"local\")\n" +
        "  viaParameter({ s -> println(\"[\" ++ s ++ \"]\") })\n" +
        "}\n"
    runSource source |> assertRan "local!\n[parameter]\n"

[<Fact>]
let ``Run - use bindings of parameterised contexts and records`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "context Ask = (ask: () -> int)\n" +
        "\n" +
        "def describe: [Show(a) + Show(b)] (a, b) -> string = { x, y -> Show(a).show(x) ++ \" & \" ++ Show(b).show(y) }\n" +
        "def twice: [Ask] () -> int = { ask() + ask() }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def unit = \"cm\"\n" +
        "  use Show(int) = ( show = { n -> \"{n}{unit}\" } )\n" +
        "  use Show(string) = ( show = { s -> s } )\n" +
        "  println(describe(3, \"three\"))\n" +
        "  def answers = (ask = { 21 })\n" +
        "  use answers\n" +
        "  println(\"{twice()}\")\n" +
        "}\n"
    runSource source |> assertRan "3cm & three\n42\n"

[<Fact>]
let ``Run - a call synthesizes a context's instance from attached definitions, and a use wins over it`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.show: Point -> string = { p -> \"({p.x}, {p.y})\" }\n" +
        "\n" +
        "type $Money = private int\n" +
        "def $Money.show: $Money -> string = { m ->\n" +
        "  def $Money(pence) = m\n" +
        "  \"{pence}p\"\n" +
        "}\n" +
        "\n" +
        "def describe: [Show(a)] a -> string = { value -> \"<\" ++ Show(a).show(value) ++ \">\" }\n" +
        "def both: [Show(a) + Show(b)] (a, b) -> string = { x, y -> Show(a).show(x) ++ \" & \" ++ Show(b).show(y) }\n" +
        "def twice: [Show(a)] a -> string = { value -> describe(value) ++ describe(value) }\n" +
        "def plain: [Show(a)] a -> string = { value -> show(value) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def p = Point(x = 1, y = 2)\n" +
        "  println(plain(p))\n" +
        "  println(describe(p))\n" +
        "  println(both($Money(5), p))\n" +
        "  println(twice(p))\n" +
        "  println(p \\describe)\n" +
        "  use Show(Point) = ( show = { q -> \"nearer {q.y}\" } )\n" +
        "  println(describe(p))\n" +
        "  println(describe($Money(7)))\n" +
        "}\n"
    runSource source
    |> assertRan "(1, 2)\n<(1, 2)>\n5p & (1, 2)\n<(1, 2)><(1, 2)>\n<(1, 2)>\n<nearer 2>\n<7p>\n"

[<Fact>]
let ``Run - contexts through the core IR, with arguments that store on the way`` () =
    // A context member called with an argument that interpolates, and a record whose field is computed through a
    // match, after another computed by a call: each operand that stores to a local is computed where it is used.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Ask = (ask: () -> int)\n" +
        "\n" +
        "def twice: [Ask] () -> int = { ask() + ask() }\n" +
        "\n" +
        "def inc: int -> int = { n -> n + 1 }\n" +
        "\n" +
        "def report: [Console] int -> () = { n -> println(\"n = \" ++ \"{n}\" ++ \"!\") }\n" +
        "\n" +
        "def pair: int -> (int, int) = { n -> (inc(n) + (match n | 0 -> 0 | m -> m * 10), 7) }\n" +
        "\n" +
        "def main = {\n" +
        "  use Ask(ask = { 21 })\n" +
        "  report(twice())\n" +
        "  report(pair(2).0)\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "n = 42!\nn = 23!\n"

[<Fact>]
let ``Run - a context member named by an operator is synthesized from an attached operator`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Eq(a) = (`==`: (a, a) -> bool)\n" +
        "\n" +
        "type $Point = (x: int, y: int)\n" +
        "def $Point.`==`: $Point, $Point -> bool = { p, q -> p.x == q.x }\n" +
        "def same: [Eq(a)] a, a -> bool = { x, y -> Eq(a).`==`(x, y) }\n" +
        "def `<+>`: int, int -> int = { a, b -> a * 10 + b }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(Int.toString(`<+>`(1, 2)))\n" +
        "  def combine = `<+>`\n" +
        "  println(Int.toString(combine(3, 4)))\n" +
        "  println(if same($Point(x = 1, y = 2), $Point(x = 1, y = 9)) then \"same\" else \"different\")\n" +
        "  println(if $Point.`==`($Point(x = 1, y = 2), $Point(x = 2, y = 2)) then \"same\" else \"different\")\n" +
        "}\n"
    runSource source |> assertRan "12\n34\nsame\ndifferent\n"

let ``Run - a lambda in a generic function calls the members of the context the function requires, and passes it on`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.show: Point -> string = { p -> \"({p.x}, {p.y})\" }\n" +
        "\n" +
        "def describe: [Show(a)] a -> string = { v -> \"<\" ++ Show(a).show(v) ++ \">\" }\n" +
        "def qualified: [Show(a)] list(a) -> string = { xs -> List.fold(xs, \"\", { acc, x -> acc ++ Show(a).show(x) }) }\n" +
        "def plain: [Show(a)] list(a) -> string = { xs -> List.fold(xs, \"\", { acc, x -> acc ++ show(x) }) }\n" +
        "def passed: [Show(a)] list(a) -> string = { xs -> List.fold(xs, \"\", { acc, x -> acc ++ describe(x) }) }\n" +
        "def nested: [Show(a)] list(a) -> string = { xs ->\n" +
        "  List.fold(xs, \"\", { acc, x -> acc ++ List.fold([1, 2], \"\", { inner, n -> inner ++ show(x) }) })\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def points = [Point(x = 1, y = 2), Point(x = 3, y = 4)]\n" +
        "  println(qualified(points))\n" +
        "  println(plain(points))\n" +
        "  println(passed(points))\n" +
        "  println(nested([Point(x = 5, y = 6)]))\n" +
        "}\n"
    runSource source |> assertRan "(1, 2)(3, 4)\n(1, 2)(3, 4)\n<(1, 2)><(3, 4)>\n(5, 6)(5, 6)\n"

/// A parameterised context, two types with instances of it, and a function that requires it, for the tests of the
/// contexts a function that does not write its type requires (#544).
let private inferredShowPrelude =
    "import nanyx/console\n" +
    "\n" +
    "context Show(a) = (show: a -> string)\n" +
    "\n" +
    "type Point = (x: int, y: int)\n" +
    "type Size = (w: int)\n" +
    "def Point.show: Point -> string = { p -> \"({p.x}, {p.y})\" }\n" +
    "def Size.show: Size -> string = { s -> \"{s.w}px\" }\n" +
    "def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }\n" +
    "def twice = { v -> describe(v) ++ describe(v) }\n" +
    "\n"

[<Fact>]
let ``Run - a function that does not write its type passes on the parameterised context it was inferred to require`` () =
    let source =
        inferredShowPrelude +
        "@entry\n" +
        "def main = { println(twice(Point(x = 1, y = 2))) }\n"
    runSource source |> assertRan "(1, 2)(1, 2)\n"

[<Fact>]
let ``Run - an inferred parameterised context passes through two functions and is used at two types`` () =
    let source =
        inferredShowPrelude +
        "def thrice = { v -> twice(v) ++ describe(v) }\n" +
        "def both = { p, s -> describe(p) ++ \" \" ++ describe(s) }\n" +
        "def logged = { v -> println(describe(v)) }\n" +
        "def lengths = { xs -> List.map(xs, { x -> describe(x) }) \\List.length }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(thrice(Point(x = 1, y = 2)))\n" +
        "  println(twice(Size(w = 3)))\n" +
        "  println(both(Point(x = 4, y = 5), Size(w = 6)))\n" +
        "  logged(Size(w = 7))\n" +
        "  println(Int.toString(lengths([Size(w = 8), Size(w = 9)])))\n" +
        "}\n"
    runSource source |> assertRan "(1, 2)(1, 2)(1, 2)\n3px3px\n(4, 5) 6px\n7px\n2\n"

[<Fact>]
let ``Run - an operator in a generic function calls the member of the context the function requires`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Add(a) = (`+`: (a, a) -> a)\n" +
        "context Sum(a) = (zero: () -> a, `+`: (a, a) -> a)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.`+`: Point, Point -> Point = { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }\n" +
        "\n" +
        "def add: [Add(a)] a, a -> a = { x, y -> x + y }\n" +
        "def addCounting: [Add(a)] a, a, int -> (a, int) = { x, y, n -> (x + y, n + 1) }\n" +
        "def sum: [Sum(a)] list(a) -> a = { items -> items \\fold(zero()) { + } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def p = add(Point(x = 1, y = 2), Point(x = 3, y = 4))\n" +
        "  println(\"{p.x} {p.y}\")\n" +
        "  use Add(string) = (`+` = { a, b -> a ++ b })\n" +
        "  def (s, n) = addCounting(\"a\", \"b\", 41)\n" +
        "  println(\"{s} {n}\")\n" +
        "  use Sum(int) = (zero = { 0 }, `+` = { a, b -> a * 10 + b })\n" +
        "  println(\"{sum([1, 2, 3])}\")\n" +
        "}\n"
    runSource source |> assertRan "4 6\nab 42\n123\n"
