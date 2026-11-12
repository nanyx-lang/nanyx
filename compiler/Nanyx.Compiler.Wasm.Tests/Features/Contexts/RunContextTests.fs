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
        "  use (write = { s -> write(\"[\" ++ s ++ \"]\") })\n" +
        "  greet(\"quiet\")\n" +
        "}\n" +
        "\n" +
        "main()\n" +
        "greet(\"loud\")\n"
    // `println` writes through whichever `write` is in scope, once for the value and once for the line break.
    runSource source |> assertRan "[quiet][\n]loud\n"

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
        "    p = write\n" +
        "  )\n" +
        "  greet(\"Nanyx\")\n" +
        "}\n"
    runSource source |> assertRan "Nanyx"

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
        "  def showInt: Show(int) = ( show = { n -> \"{n}{unit}\" } )\n" +
        "  use showInt\n" +
        "  def showString: Show(string) = ( show = { s -> s } )\n" +
        "  use showString\n" +
        "  println(describe(3, \"three\"))\n" +
        "  def answers = (ask = { 21 })\n" +
        "  use answers\n" +
        "  println(\"{twice()}\")\n" +
        "}\n"
    runSource source |> assertRan "3cm & three\n42\n"

[<Fact>]
let ``Run - a use of a record value of a parameterised context's shape loads it as the instance`` () =
    // `int` has its own `+`, which an instance found by the record's shape is nearer than (#581).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Add(a) = (`+`: (a, a) -> a)\n" +
        "\n" +
        "def add: [Add(a)] a, a -> a = { x, y -> `+`(x, y) }\n" +
        "\n" +
        "def named = {\n" +
        "  def addInt: Add(int) = ( `+` = { x, y -> x * 10 + y } )\n" +
        "  use addInt\n" +
        "  add(1, 2)\n" +
        "}\n" +
        "\n" +
        "def written = {\n" +
        "  use ( `+` = { x, y -> x * 100 + y } )\n" +
        "  add(3, 4)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = { println(\"{named()} {written()}\") }\n"
    runSource source |> assertRan "12 304\n"

[<Fact>]
let ``Run - use loads a named instance of a parameterised context: a def, a local, a parameter or a call's result (#581)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Combine(a) = (combine: (a, a) -> a)\n" +
        "\n" +
        "def double: [Combine(a)] a -> a = { x -> Combine(a).combine(x, x) }\n" +
        "def timesInt: Combine(int) = (combine = { a, b -> a * b })\n" +
        "def plusAnd: int -> Combine(int) = { k -> (combine = { a, b -> a + b + k }) }\n" +
        "def viaParameter: (Combine(int), int) -> int = { instance, x ->\n" +
        "  use instance\n" +
        "  double(x)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def plusInt: Combine(int) = (combine = { a, b -> a + b })\n" +
        "  use plusInt\n" +
        "  println(\"{double(5)}\")\n" +
        "  use timesInt\n" +
        "  println(\"{double(5)}\")\n" +
        "  println(\"{viaParameter(plusInt, 4)}\")\n" +
        "  use plusAnd(100)\n" +
        "  println(\"{double(1)}\")\n" +
        "}\n"
    runSource source |> assertRan "10\n25\n8\n102\n"

[<Fact>]
let ``Run - use loads a top-level instance of a parameterised context (#587)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Add(a) = (\n" +
        "  `+`: (a, a) -> a\n" +
        ")\n" +
        "\n" +
        "def add: [Add(a)] a, a -> a = { x, y -> `+`(x, y) }\n" +
        "\n" +
        "def addInt: Add(int) = (\n" +
        "  `+` = { x, y -> x * 10 + y }\n" +
        ")\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  use addInt\n" +
        "  println(\"{add(1, 2)}\")\n" +
        "}\n"
    runSource source |> assertRan "12\n"

[<Fact>]
let ``Run - use loads a top-level record of a plain context (#587)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Greeter = (greet: string -> string)\n" +
        "\n" +
        "def hello: [Greeter] string -> string = { name -> greet(name) }\n" +
        "\n" +
        "def polite: Greeter = (greet = { name -> \"Good day, {name}\" })\n" +
        "\n" +
        "def casual: Greeter = (greet = { name -> \"Hi {name}\" })\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  use polite\n" +
        "  println(hello(\"Ann\"))\n" +
        "  println(use casual in hello(\"Bo\"))\n" +
        "}\n"
    runSource source |> assertRan "Good day, Ann\nHi Bo\n"

[<Fact>]
let ``Run - use ... in loads an instance for its body only (#320)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Counter = (next: int -> int)\n" +
        "context Combine(a) = (combine: (a, a) -> a)\n" +
        "\n" +
        "def counted: [Counter] int -> int = { n -> next(n) }\n" +
        "def double: [Combine(a)] a -> a = { x -> Combine(a).combine(x, x) }\n" +
        "def timesInt: Combine(int) = (combine = { a, b -> a * b })\n" +
        "def plusInt: Combine(int) = (combine = { a, b -> a + b })\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def total = use Counter(next = { n -> n - 1 }) in counted(3)\n" +
        "  println(\"{total}\")\n" +
        "  use plusInt\n" +
        "  println(\"{use timesInt in double(5)}\")\n" +
        "  println(\"{double(5)}\")\n" +
        "  def onTheNextLine = use timesInt in\n" +
        "    double(2)\n" +
        "  println(\"{onTheNextLine}\")\n" +
        "}\n"
    runSource source |> assertRan "2\n25\n10\n4\n"

[<Fact>]
let ``Run - use ... in runs an indented block after in with the context loaded (#673)`` () =
    let source =
        """import nanyx/console

context Counter = (next: int -> int)

def counted: [Counter] int -> int = { n -> next(n) }

@entry
def main = {
  def offset = 10
  def total = use Counter(next = { n -> n - 1 }) in
    def a = counted(3)
    a + offset
  println("{total}")
  use Counter(next = { n -> n * 2 }) in
    def b = counted(4)
    println("{b}")
}
"""
    runSource source |> assertRan "12\n8\n"

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
        "  def nearer: Show(Point) = ( show = { q -> \"nearer {q.y}\" } )\n" +
        "  use nearer\n" +
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
        "  println(int.toString(`<+>`(1, 2)))\n" +
        "  def combine = `<+>`\n" +
        "  println(int.toString(combine(3, 4)))\n" +
        "  println(if same($Point(x = 1, y = 2), $Point(x = 1, y = 9)) then \"same\" else \"different\")\n" +
        "  println(if $Point.`==`($Point(x = 1, y = 2), $Point(x = 2, y = 2)) then \"same\" else \"different\")\n" +
        "}\n"
    runSource source |> assertRan "12\n34\nsame\ndifferent\n"

[<Fact>]
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
        "def qualified: [Show(a)] list(a) -> string = { xs -> list.fold(xs, \"\", { acc, x -> acc ++ Show(a).show(x) }) }\n" +
        "def plain: [Show(a)] list(a) -> string = { xs -> list.fold(xs, \"\", { acc, x -> acc ++ show(x) }) }\n" +
        "def passed: [Show(a)] list(a) -> string = { xs -> list.fold(xs, \"\", { acc, x -> acc ++ describe(x) }) }\n" +
        "def nested: [Show(a)] list(a) -> string = { xs ->\n" +
        "  list.fold(xs, \"\", { acc, x -> acc ++ list.fold([1, 2], \"\", { inner, n -> inner ++ show(x) }) })\n" +
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
        "def lengths = { xs -> list.map(xs, { x -> describe(x) }) \\list.length }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(thrice(Point(x = 1, y = 2)))\n" +
        "  println(twice(Size(w = 3)))\n" +
        "  println(both(Point(x = 4, y = 5), Size(w = 6)))\n" +
        "  logged(Size(w = 7))\n" +
        "  println(int.toString(lengths([Size(w = 8), Size(w = 9)])))\n" +
        "}\n"
    runSource source |> assertRan "(1, 2)(1, 2)(1, 2)\n3px3px\n(4, 5) 6px\n7px\n2\n"

/// `Sum`, whose `` `0` `` is a value rather than a function, with an instance for `Point` synthesized from its attached
/// definitions, for the tests of context members used as values (#580).
let private sumPrelude =
    "import nanyx/console\n" +
    "\n" +
    "context Sum(a) = (`0`: a, `+`: (a, a) -> a)\n" +
    "\n" +
    "type Point = (x: int, y: int)\n" +
    "def Point.`0`: Point = Point(x = 0, y = 0)\n" +
    "def Point.`+`: Point, Point -> Point = { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }\n" +
    "\n"

[<Fact>]
let ``Run - a context member that is a value is passed from a synthesized instance`` () =
    let source =
        sumPrelude +
        "def sum: [Sum(a)] list(a) -> a = { items -> items \\fold(`0`) { + } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])\n" +
        "  println(\"{total.x}, {total.y}\")\n" +
        "}\n"
    runSource source |> assertRan "9, 12\n"

[<Fact>]
let ``Run - a context member that is a value is named through its context`` () =
    let source =
        sumPrelude +
        "def sum: [Sum(a)] list(a) -> a = { items -> items \\fold(Sum(a).`0`) { a, b -> Sum(a).`+`(a, b) } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4)])\n" +
        "  println(\"{total.x}, {total.y}\")\n" +
        "}\n"
    runSource source |> assertRan "4, 6\n"

[<Fact>]
let ``Run - a synthesized instance from attached definitions that do not write their types`` () =
    // `Point.`+`` reads the fields of any record that has them, so it is generic: it is passed at the member's type.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Sum(a) = (`0`: a, `+`: (a, a) -> a)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.`0` = Point(x = 0, y = 0)\n" +
        "def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }\n" +
        "def sum: [Sum(a)] list(a) -> a = { items -> items \\fold(`0`) { a, b -> `+`(a, b) } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])\n" +
        "  println(\"{total.x}, {total.y}\")\n" +
        "}\n"
    runSource source |> assertRan "9, 12\n"

[<Fact>]
let ``Run - a function member of no parameters is passed from a synthesized instance`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Zero(a) = (zero: () -> a)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.zero: () -> Point = { Point(x = 7, y = 8) }\n" +
        "def origin: [Zero(a)] () -> a = { zero() }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def p: Point = origin()\n" +
        "  println(\"{p.x}, {p.y}\")\n" +
        "}\n"
    runSource source |> assertRan "7, 8\n"

[<Fact>]
let ``Run - a function member is passed on as a value, by its name and through its context`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.show: Point -> string = { p -> \"({p.x}, {p.y})\" }\n" +
        "def concat: list(string) -> string = { xs -> xs \\list.fold(\"\") { acc, s -> acc ++ s } }\n" +
        "def shown: [Show(a)] list(a) -> string = { xs -> concat(list.map(xs, show)) }\n" +
        "def qualified: [Show(a)] list(a) -> string = { xs -> concat(list.map(xs, Show(a).show)) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(shown([Point(x = 1, y = 2), Point(x = 3, y = 4)]))\n" +
        "  println(qualified([Point(x = 5, y = 6)]))\n" +
        "}\n"
    runSource source |> assertRan "(1, 2)(3, 4)\n(5, 6)\n"

[<Fact>]
let ``Run - an operator in a generic function calls the member of the context the function requires`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Add(a) = (`+`: (a, a) -> a)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.`+`: Point, Point -> Point = { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }\n" +
        "\n" +
        "def add: [Add(a)] a, a -> a = { x, y -> x + y }\n" +
        "def addCounting: [Add(a)] a, a, int -> (a, int) = { x, y, n -> (x + y, n + 1) }\n" +
        "def sum: [Add(a)] a, list(a) -> a = { start, items -> items \\fold(start) { + } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def p = add(Point(x = 1, y = 2), Point(x = 3, y = 4))\n" +
        "  println(\"{p.x} {p.y}\")\n" +
        "  def (q, n) = addCounting(p, Point(x = 10, y = 20), 41)\n" +
        "  println(\"{q.x} {q.y} {n}\")\n" +
        "  def total = sum(Point(x = 0, y = 0), [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])\n" +
        "  println(\"{total.x} {total.y}\")\n" +
        "}\n"
    runSource source |> assertRan "4 6\n14 26 42\n9 12\n"

// An imported function that requires a context its module does not export finds the instance where it is called, from
// the importer's attached definitions, though the importer cannot name the context (#551).
[<Fact>]
let ``Run - an imported function requiring an unexported parameterised context`` () =
    let lib = """module lib
context Show(a) = (show: a -> string)
export def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
"""
    let main = """import lib
type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
@entry
def main = { println(describe(Point(x = 1, y = 2))) }
"""
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "lib.nyx"), lib)
    let mainPath = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(mainPath, main)
    runFile mainPath |> assertRan "(1, 2)\n"

[<Fact>]
let ``Run - a lambda that uses a context runs inside the def that supplies it (#774)`` () =
    let source =
        """import nanyx/console

context Foo = (bar: string -> string)

def inside: [Foo] string -> string = { s ->
  def f = { bar(s) }
  def twice = { x -> bar(bar(x)) }
  f() ++ " " ++ twice("y") ++ " " ++ ([1, 2] \list.map { _ -> bar("z") } \list.length \int.toString)
}

println(use Foo(bar = { s -> s ++ "!" }) in inside("ok"))
"""
    runSource source |> assertRan "ok! y!! 2\n"
