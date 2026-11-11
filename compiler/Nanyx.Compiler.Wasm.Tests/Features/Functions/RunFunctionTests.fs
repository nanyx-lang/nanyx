module TranspilerWasmTests.Features.Functions.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a lambda's parameters may be patterns that take its argument apart`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type $Email = string\n" +
        "\n" +
        "def address: $Email -> string = { $Email(s) -> s }\n" +
        "def weigh = { (a, b), c -> a + b * c }\n" +
        "def answer = { () -> 42 }\n" +
        "def middle = { (), (_, (m, _)) -> m }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(address($Email(\"ada@example.com\")))\n" +
        "  println(\"{weigh((1, 2), 3)} {answer()} {middle((), (0, (\"mid\", 1)))}\")\n" +
        "  def sums = [(1, 2), (30, 40)] \\map { (x, y) -> x + y }\n" +
        "  for sums | n -> println(\"{n}\")\n" +
        "  def offset = 100\n" +
        "  def shifted = [(1, 2)] \\map { (x, y) ->\n" +
        "    def total = x + y\n" +
        "    total + offset\n" +
        "  }\n" +
        "  for shifted | n -> println(\"{n}\")\n" +
        "}\n"
    runSource source |> assertRan "ada@example.com\n7 42 mid\n3\n70\n103\n"

[<Fact>]
let ``Run - local defs in a pattern lambda's arms`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def f: [Console] list(int) -> () = {\n" +
        "  | [] ->\n" +
        "    def done = \"end\"\n" +
        "    println(done)\n" +
        "  | [n, ...rest] ->\n" +
        "    def label = \"x\"\n" +
        "    def doubled = n * 2\n" +
        "    println(label ++ \"{doubled}\")\n" +
        "    f(rest)\n" +
        "}\n" +
        "\n" +
        "def g: [Console] (int, string) -> () = {\n" +
        "  | 0, s ->\n" +
        "    def last = s ++ \"!\"\n" +
        "    println(last)\n" +
        "  | n, s ->\n" +
        "    def next = s ++ \".\"\n" +
        "    g(n - 1, next)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  f([1, 2])\n" +
        "  g(3, \"go\")\n" +
        "}\n"
    runSource source |> assertRan "x2\nx4\nend\ngo...!\n"

[<Fact>]
let ``Run - a pipe passes its value in front of several arguments`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def digits: int, int, int -> int = { a, b, c -> a * 100 + b * 10 + c }\n" +
        "def digitsThen: int, int, int, (int -> int) -> int = { a, b, c, h -> h(digits(a, b, c)) }\n" +
        "\n" +
        "println(\"{1 \\digits(2, 3)}\")\n" +
        "println(\"{1 \\digitsThen(2, 3) { + 1000 }}\")\n"
    runSource source |> assertRan "123\n1123\n"

[<Fact>]
let ``Run - shorthand lambdas filter, map and fold a list`` () =
    // `{ % 2 == 0 }` is `{ x -> x % 2 == 0 }`: the body goes on after the operation on the left-out argument.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def items = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
        "def x = 100\n" +
        "\n" +
        "def items2 = items \\filter { % 2 == 0 }\n" +
        "\n" +
        "for items2 | i -> println(\"{i}\")\n" +
        "for items \\filter { > 8 } \\map { * x + 1 } | i -> println(\"{i}\")\n" +
        "println(\"{items \\fold(0) { + }}\")\n" +
        "println(\"{[\"a\", \"\", \"bc\"] \\filter { != \"\" } \\map { .length } \\fold(0) { + }}\")\n"
    runSource source |> assertRan "2\n4\n6\n8\n10\n901\n1001\n55\n3\n"

[<Fact>]
let ``Run - a shorthand lambda compares a field of its argument`` () =
    // `{ .age > 18 }` is `{ p -> p.age > 18 }`: the field access is the left operand of what follows it.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type P = (name: string, age: int)\n" +
        "\n" +
        "def ps: list(P) = [P(name = \"a\", age = 12), P(name = \"b\", age = 30), P(name = \"c\", age = 40)]\n" +
        "\n" +
        "println(\"{ps \\list.filter { .name == \"b\" } \\list.length}\")\n" +
        "for ps \\list.filter { .age > 18 } | p -> println(p.name)\n" +
        "println(\"{ps \\list.filter { .age * 2 >= 60 } \\list.length}\")\n"
    runSource source |> assertRan "1\nb\nc\n2\n"

[<Fact>]
let ``Run - a function takes positional and named arguments together, called or piped into`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }\n" +
        "def digits: (int, int, last: int) -> int = { a, b, last -> a * 100 + b * 10 + last }\n" +
        "\n" +
        "println(\"{stepped(1, step = 2)}\")\n" +
        "println(\"{1 \\stepped(step = 2)}\")\n" +
        "println(\"{digits(1, 2, last = 3)}\")\n" +
        "println(\"{1 \\digits(2, last = 3)}\")\n"
    runSource source |> assertRan "12\n12\n123\n123\n"

[<Fact>]
let ``Run - multi-argument pattern lambdas`` () =
    // The two-parameter `{ | [], _ -> ... | [head, ...tail], f -> ... }` form from the pattern-matching page (#45).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def map: (list(int), (int -> int)) -> list(int) = {\n" +
        "  | [], _ -> []\n" +
        "  | [head, ...tail], f -> [f(head), ...map(tail, f)]\n" +
        "}\n" +
        "def sum: list(int) -> int = {\n" +
        "  | [] -> 0\n" +
        "  | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{sum(map([1, 2, 3], { * 10 }))}\")\n" +
        "}\n"
    runSource source |> assertRan "60\n"

[<Fact>]
let ``Run - an annotated zero-parameter function returning a tag union`` () =
    // The `() -> ...` annotation names a unit input for a lambda with no parameters; the backend used to
    // crash with a negative padding count instead of compiling it.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getMessage: () -> #some(string) | #noMessage = {\n" +
        "  def message = \"Hello, from Nanyx!\"\n" +
        "  #some(message)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match getMessage()\n" +
        "    | #some(_) -> println(\"There was a message\")\n" +
        "    | #noMessage -> println(\"No message available\")\n" +
        "}\n"
    runSource source |> assertRan "There was a message\n"

[<Fact>]
let ``Run - a function annotated as returning a function returns a closure`` () =
    // `int -> (int -> int)` is one parameter returning a function; the backend used to crash with a
    // negative padding count on the annotation, then reported closures as unsupported (#29).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def add5 = makeAdder(5)\n" +
        "  dbg(add5(10))\n" +
        "}\n"
    runSource source |> assertRan "dbg: 15\n"

[<Fact>]
let ``Run - a lambda argument's inferred parameter types`` () =
    // A lambda passed to a call is lifted to a function of its own; its parameters' types come from the typed
    // call it was an argument of, so `p.x` has a record layout and `"{n}"` an interpolation type.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type P = (x: int)\n" +
        "def apply: P, (P -> int) -> int = { p, f -> f(p) }\n" +
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def n = P(x = 3) \\apply { p -> p.x }\n" +
        "  def x: #some(int) | #notFound = #some(n)\n" +
        "  def m = x \\mapOption { v -> \"got {v}\" }\n" +
        "  match m\n" +
        "    | #some(s) -> println(s)\n" +
        "    | #notFound -> println(\"none\")\n" +
        "}\n"
    runSource source |> assertRan "got 3\n"

[<Fact>]
let ``Run - calling a unit function whose lambda takes a parameter`` () =
    // `{ _ -> ... }` has one parameter, the unit, which `getData()` passes.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getData: () -> #some(int) | #notFound = { _ -> #some(42) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match getData()\n" +
        "    | #some(n) -> println(\"got {n}\")\n" +
        "    | #notFound -> println(\"none\")\n" +
        "}\n"
    runSource source |> assertRan "got 42\n"

[<Fact>]
let ``Run - mutually recursive top-level functions`` () =
    // Regression: a def could not call one declared later in the file (#47).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }\n" +
        "def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "show(isEven(10))\n" +
        "show(isOdd(7))\n" +
        "show(isEven(7))\n" +
        "dbg(later(2))\n" +
        "def later = { x -> x * 21 }\n"
    runSource source |> assertRan "yes\nyes\nno\ndbg: 42\n"

[<Fact>]
let ``Run - functions as values and higher-order calls`` () =
    // #28: a parameter holding a function can be called; a named function or a lambda can be passed.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def apply: ((int -> int), int) -> int = { f, x -> f(x) }\n" +
        "def twice: ((int -> int), int) -> int = { f, x -> f(f(x)) }\n" +
        "def double: int -> int = { x -> x * 2 }\n" +
        "def map: (list(int), (int -> int)) -> list(int) = { xs, f ->\n" +
        "  match xs\n" +
        "    | [] -> []\n" +
        "    | [head, ...tail] -> [f(head)] ++ map(tail, f)\n" +
        "}\n" +
        "def sum: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> 0\n" +
        "    | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "def main = {\n" +
        "  def square = { x -> x * x }\n" +
        "  dbg(apply(double, 21))\n" +
        "  dbg(apply({ x -> x + 1 }, 41))\n" +
        "  dbg(apply({ * 3 }, 14))\n" +
        "  dbg(twice(double, 10))\n" +
        "  dbg(square(6))\n" +
        "  dbg(sum(map([1, 2, 3], double)))\n" +
        "  dbg(sum([1, 2, 3] \\map { * 10 }))\n" +
        "}\n" +
        "main()\n"
    runSource source |> assertRan "dbg: 42\ndbg: 42\ndbg: 42\ndbg: 40\ndbg: 36\ndbg: 12\ndbg: 60\n"

[<Fact>]
let ``Run - an attached function passed as a value`` () =
    // `Point.norm` as an argument is read as a member access on `Point`, not as the name of a function.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.norm: Point -> int = { p -> p.x + p.y }\n" +
        "def apply: ((Point -> int), Point) -> int = { f, p -> f(p) }\n" +
        "dbg(apply(Point.norm, (x = 3, y = 4)))\n" +
        "dbg(list.fold([(x = 1, y = 2), (x = 10, y = 20)] \\list.map(Point.norm), 0, { a, b -> a + b }))\n"
    runSource source |> assertRan "dbg: 7\ndbg: 33\n"

[<Fact>]
let ``Run - a capturing lambda passed to a function`` () =
    let source =
        "def apply = { f, x -> f(x) }\n" +
        "def main = { n ->\n" +
        "  apply({ x -> x + n }, 1)\n" +
        "}\n" +
        "dbg(main(2))\n"
    runSource source |> assertRan "dbg: 3\n"

[<Fact>]
let ``Run - closures`` () =
    // Closures capture locals, parameters and other closures; captured strings and records keep their types.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }\n" +
        "def twice: ((int -> int), int) -> int = { f, v -> f(f(v)) }\n" +
        "def greeter: string -> (string -> string) = { greeting -> { name -> greeting ++ \", \" ++ name } }\n" +
        "type Point = (x: int, y: int)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def add5 = makeAdder(5)\n" +
        "  println(\"{add5(10)} {twice(add5, 1)} {twice(makeAdder(1), 0)}\")\n" +
        "  def hello = greeter(\"Hello\")\n" +
        "  println(hello(\"Ada\"))\n" +
        "  def origin = Point(x = 1, y = 2)\n" +
        "  def shiftX = { d -> origin.x + d }\n" +
        "  def compose = { g -> { v -> add5(g(v)) } }\n" +
        "  def shiftedThenAdd5 = compose(shiftX)\n" +
        "  def doubledThenAdd5 = compose({ v -> v * 2 })\n" +
        "  println(\"{shiftX(10)}\")\n" +
        "  dbg(shiftedThenAdd5(1))\n" +
        "  dbg(doubledThenAdd5(3))\n" +
        "  def limit = 3\n" +
        "  rec countTo = { i -> if i < limit then countTo(i + 1) else i }\n" +
        "  println(\"{countTo(0)}\")\n" +
        "}\n"
    runSource source |> assertRan "15 11 2\nHello, Ada\n11\ndbg: 7\ndbg: 11\n3\n"

[<Fact>]
let ``Run - a function that only calls the closure it is given compiles without making one`` () =
    let source =
        "def apply: (int -> int), int -> int = { f, x -> f(x) }\n" +
        "\n" +
        "@entry\n" +
        "def main = { }\n"
    runSource source |> assertRan ""

[<Fact>]
let ``Run - a generic function specialised at two types gives each its own result`` () =
    // `orElse` is specialised for ints and for strings, and `main` calls both specialisations (see CoreIrTests.fs).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def orElse = { o, fallback ->\n" +
        "  match o\n" +
        "    | #some(v) -> v\n" +
        "    | #none -> fallback\n" +
        "}\n" +
        "\n" +
        "def pick = { n -> if n > 0 then #some(n) else #none }\n" +
        "\n" +
        "def name = { n -> if n > 0 then #some(\"positive\") else #none }\n" +
        "\n" +
        "def describe = { n -> \"{n * 1}\" }\n" +
        "\n" +
        "def main = {\n" +
        "  println(describe(orElse(pick(5), 0)))\n" +
        "  println(describe(orElse(pick(-5), 0)))\n" +
        "  println(orElse(name(1), \"none\"))\n" +
        "  println(orElse(name(0), \"none\"))\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "5\n0\npositive\nnone\n"

[<Fact>]
let ``Run - closures through the core IR`` () =
    // `compose`'s lambda is specialised along with `compose` (see CoreIrTests.fs).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def apply = { f, x -> f(x) }\n" +
        "\n" +
        "def double: int -> int = { n -> n * 2 }\n" +
        "\n" +
        "def compose = { f, g -> { x -> g(f(x)) } }\n" +
        "\n" +
        "def countdown: int -> int = { n ->\n" +
        "  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + i) }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "\n" +
        "def show = { n -> \"{n * 1}\" }\n" +
        "\n" +
        "def main = {\n" +
        "  def offset = 10\n" +
        "  def both = compose(double, { x -> x + offset })\n" +
        "  println(show(apply({ x -> x + offset }, 1)))\n" +
        "  println(show(apply(double, 4)))\n" +
        "  println(show(both(1)))\n" +
        "  println(apply({ s -> s ++ \"!\" }, \"hi\"))\n" +
        "  println(show(countdown(4)))\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "11\n8\n12\nhi!\n10\n"

[<Fact>]
let ``Run - holes in annotations are filled in by inference`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def add: (int, _) -> _ = { a, b -> a + b }\n" +
        "def names: list(_) = [\"ada\", \"grace\"]\n" +
        "def id: _ -> _ = { x -> x }\n" +
        "def greet = { (name: _) -> \"hi {name}\" }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def total: _ = add(40, 2)\n" +
        "  println(\"{total} {id(7)} {id(\"same\")}\")\n" +
        "  for names | n -> println(greet(n))\n" +
        "}\n"
    runSource source |> assertRan "42 7 same\nhi ada\nhi grace\n"

[<Fact>]
let ``Run - a local lambda of no parameters runs each time it is called (#589)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x = 10\n" +
        "  def block = {\n" +
        "    def a = 3\n" +
        "    println(\"ran\")\n" +
        "    a + x\n" +
        "  }\n" +
        "  println(\"before\")\n" +
        "  println(\"{block()}\")\n" +
        "  println(\"{block()}\")\n" +
        "  def nothing = {}\n" +
        "  nothing()\n" +
        "}\n"
    runSource source |> assertRan "before\nran\n13\nran\n13\n"

[<Fact>]
let ``Run - a lambda of no parameters can be passed as a value (#589)`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def twice: (() -> int) -> int = { f -> f() + f() }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x = 10\n" +
        "  def next = { x + 1 }\n" +
        "  println(\"{twice(next)} {twice({ x * 2 })}\")\n" +
        "  def thunks = [{ x }, { x - 1 }]\n" +
        "  for thunks | t -> println(\"{t()}\")\n" +
        "}\n"
    runSource source |> assertRan "22 40\n10\n9\n"

[<Fact>]
let ``Run - a lambda of no parameters passed as an argument is called later, not run where it is written (#182)`` () =
    let source = """import nanyx/console

def mk: (() -> b) -> b = { f -> f() }
def m = mk({ 7 })
def lazy: bool, (() -> int) -> int = { run, f -> if run then f() else 0 }

@entry
def main = {
  println("{m} {mk({ 8 })}")
  println("{lazy(false, { dbg(1) })} {lazy(true, { dbg(2) })}")
  println("{mapOrOption(#none, 7, { n -> n + 1 })}")
}
"""
    runSource source |> assertRan "7 8\ndbg: 2\n0 2\n7\n"

[<Fact>]
let ``Run - a lambda of no parameters can be stored in a record field or returned from a function (#182)`` () =
    let source = """import nanyx/console

type Lazy = (get: () -> int)
def constant: int -> (() -> int) = { n -> { n * 2 } }
def lookup: () -> int = { 11 }

@entry
def main = {
  def l = Lazy(get = { 5 })
  def g = l.get
  def c = constant(3)
  def named = Lazy(get = lookup)
  def h = named.get
  println("{g()} {c()} {c()} {h()}")
}
"""
    runSource source |> assertRan "5 6 6 11\n"

[<Fact>]
let ``Run - calling the function a top-level value holds evaluates the value once`` () =
    let source =
        "def makeAdder = { n -> { m -> n + m } }\n" +
        "def adder = makeAdder(dbg(1))\n" +
        "dbg(adder(2))\n"
    runSource source |> assertRan "dbg: 1\ndbg: 3\n"

[<Fact>]
let ``Run - a trailing lambda after several arguments is the call's last argument`` () =
    let source = """import nanyx/console

def combine: int, int, (int, int -> int) -> int = { a, b, g -> g(a, b) }
def scaled: (int, (int -> int), scale: int) -> int = { a, g, scale -> g(a) * scale }

@entry
def main = {
  println("{list.fold([1, 2, 3], 10) { acc, x -> acc + x }}")
  println("{combine(3, 4) { a, b -> a * b }}")
  println("{scaled(5, scale = 2) { x -> x + 1 }}")
  println("{list.unfold((0, 1)) { a, b -> if a > 10 then #done else #some(a, (b, a + b)) } \sum}")
}
"""
    runSource source |> assertRan "16\n12\n12\n20\n"
