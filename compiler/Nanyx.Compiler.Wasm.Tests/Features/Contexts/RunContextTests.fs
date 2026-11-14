module TranspilerWasmTests.Features.Contexts.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a function requiring Console gets the runtime's instance`` () =
    let source =
        normalized """import nanyx/console

def greet: [Console] string -> () = { name -> println("Hello, " ++ name) }

greet("Nanyx")
"""
    runSource source |> assertRan "Hello, Nanyx\n"

[<Fact>]
let ``Run - a use binding overrides the runtime's Console`` () =
    let source =
        normalized """import nanyx/console

def greet: [Console] string -> () = { name -> println(name) }

def main = {
  use (write = { s -> write("[" ++ s ++ "]") })
  greet("quiet")
}

main()
greet("loud")
"""
    // `println` writes through whichever `write` is in scope, once for the value and once for the line break.
    runSource source |> assertRan "[quiet][\n]loud\n"

/// A context with one member, and a function that requires it, for the tests of what a `use` can bind (#88).
let private myConsolePrelude =
    normalized """import nanyx/console

context MyConsole = (p: [Console] string -> ())

def greet: [MyConsole & Console] string -> () = { name -> p(name) }

"""

[<Fact>]
let ``Run - a top-level function as a use binding`` () =
    let source =
        myConsolePrelude +
        normalized """@entry
def main = {
  use MyConsole(
    p = write
  )
  greet("Nanyx")
}
"""
    runSource source |> assertRan "Nanyx"

[<Fact>]
let ``Run - a use binding lambda that captures a local`` () =
    let source =
        myConsolePrelude +
        normalized """@entry
def main = {
  def prefix = ">> "
  use MyConsole(
    p = { s -> println("{prefix}{s}") }
  )
  greet("captured")
}
"""
    runSource source |> assertRan ">> captured\n"

[<Fact>]
let ``Run - a parameter or local holding a function as a use binding`` () =
    let source =
        myConsolePrelude +
        normalized """def viaParameter: [Console] ([Console] string -> ()) -> () = { f ->
  use MyConsole(p = f)
  greet("parameter")
}
@entry
def main = {
  def shout = { s -> println(s ++ "!") }
  use MyConsole(p = shout)
  greet("local")
  viaParameter({ s -> println("[" ++ s ++ "]") })
}
"""
    runSource source |> assertRan "local!\n[parameter]\n"

[<Fact>]
let ``Run - use bindings of parameterised contexts and records`` () =
    let source =
        normalized """import nanyx/console

context Show(a) = (show: a -> string)
context Ask = (ask: () -> int)

def describe: [Show(a) & Show(b)] (a, b) -> string = { x, y -> Show(a).show(x) ++ " & " ++ Show(b).show(y) }
def twice: [Ask] () -> int = { ask() + ask() }

@entry
def main = {
  def unit = "cm"
  def showInt: Show(int) = ( show = { n -> "{n}{unit}" } )
  use showInt
  def showString: Show(string) = ( show = { s -> s } )
  use showString
  println(describe(3, "three"))
  def answers = (ask = { 21 })
  use answers
  println("{twice()}")
}
"""
    runSource source |> assertRan "3cm & three\n42\n"

[<Fact>]
let ``Run - a use of a record value of a parameterised context's shape loads it as the instance`` () =
    // `int` has its own `+`, which an instance found by the record's shape is nearer than (#581).
    let source =
        normalized """import nanyx/console

context Add(a) = (`+`: (a, a) -> a)

def add: [Add(a)] a, a -> a = { x, y -> `+`(x, y) }

def named = {
  def addInt: Add(int) = ( `+` = { x, y -> x * 10 + y } )
  use addInt
  add(1, 2)
}

def written = {
  use ( `+` = { x, y -> x * 100 + y } )
  add(3, 4)
}

@entry
def main = { println("{named()} {written()}") }
"""
    runSource source |> assertRan "12 304\n"

[<Fact>]
let ``Run - use loads a named instance of a parameterised context: a def, a local, a parameter or a call's result (#581)`` () =
    let source =
        normalized """import nanyx/console

context Combine(a) = (combine: (a, a) -> a)

def double: [Combine(a)] a -> a = { x -> Combine(a).combine(x, x) }
def timesInt: Combine(int) = (combine = { a, b -> a * b })
def plusAnd: int -> Combine(int) = { k -> (combine = { a, b -> a + b + k }) }
def viaParameter: (Combine(int), int) -> int = { instance, x ->
  use instance
  double(x)
}

@entry
def main = {
  def plusInt: Combine(int) = (combine = { a, b -> a + b })
  use plusInt
  println("{double(5)}")
  use timesInt
  println("{double(5)}")
  println("{viaParameter(plusInt, 4)}")
  use plusAnd(100)
  println("{double(1)}")
}
"""
    runSource source |> assertRan "10\n25\n8\n102\n"

[<Fact>]
let ``Run - use loads a top-level instance of a parameterised context (#587)`` () =
    let source =
        normalized """import nanyx/console

context Add(a) = (
  `+`: (a, a) -> a
)

def add: [Add(a)] a, a -> a = { x, y -> `+`(x, y) }

def addInt: Add(int) = (
  `+` = { x, y -> x * 10 + y }
)

@entry
def main = {
  use addInt
  println("{add(1, 2)}")
}
"""
    runSource source |> assertRan "12\n"

[<Fact>]
let ``Run - use loads a top-level record of a plain context (#587)`` () =
    let source =
        normalized """import nanyx/console

context Greeter = (greet: string -> string)

def hello: [Greeter] string -> string = { name -> greet(name) }

def polite: Greeter = (greet = { name -> "Good day, {name}" })

def casual: Greeter = (greet = { name -> "Hi {name}" })

@entry
def main = {
  use polite
  println(hello("Ann"))
  println(use casual in hello("Bo"))
}
"""
    runSource source |> assertRan "Good day, Ann\nHi Bo\n"

[<Fact>]
let ``Run - use ... in loads an instance for its body only (#320)`` () =
    let source =
        normalized """import nanyx/console

context Counter = (next: int -> int)
context Combine(a) = (combine: (a, a) -> a)

def counted: [Counter] int -> int = { n -> next(n) }
def double: [Combine(a)] a -> a = { x -> Combine(a).combine(x, x) }
def timesInt: Combine(int) = (combine = { a, b -> a * b })
def plusInt: Combine(int) = (combine = { a, b -> a + b })

@entry
def main = {
  def total = use Counter(next = { n -> n - 1 }) in counted(3)
  println("{total}")
  use plusInt
  println("{use timesInt in double(5)}")
  println("{double(5)}")
  def onTheNextLine = use timesInt in
    double(2)
  println("{onTheNextLine}")
}
"""
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
        normalized """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }

type Money = private int
def Money.show: Money -> string = { m ->
  def Money(pence) = m
  "{pence}p"
}

def describe: [Show(a)] a -> string = { value -> "<" ++ Show(a).show(value) ++ ">" }
def both: [Show(a) & Show(b)] (a, b) -> string = { x, y -> Show(a).show(x) ++ " & " ++ Show(b).show(y) }
def twice: [Show(a)] a -> string = { value -> describe(value) ++ describe(value) }
def plain: [Show(a)] a -> string = { value -> show(value) }

@entry
def main = {
  def p = Point(x = 1, y = 2)
  println(plain(p))
  println(describe(p))
  println(both(Money(5), p))
  println(twice(p))
  println(p \describe)
  def nearer: Show(Point) = ( show = { q -> "nearer {q.y}" } )
  use nearer
  println(describe(p))
  println(describe(Money(7)))
}
"""
    runSource source
    |> assertRan "(1, 2)\n<(1, 2)>\n5p & (1, 2)\n<(1, 2)><(1, 2)>\n<(1, 2)>\n<nearer 2>\n<7p>\n"

[<Fact>]
let ``Run - contexts through the core IR, with arguments that store on the way`` () =
    // A context member called with an argument that interpolates, and a record whose field is computed through a
    // match, after another computed by a call: each operand that stores to a local is computed where it is used.
    let source =
        normalized """import nanyx/console

context Ask = (ask: () -> int)

def twice: [Ask] () -> int = { ask() + ask() }

def inc: int -> int = { n -> n + 1 }

def report: [Console] int -> () = { n -> println("n = " ++ "{n}" ++ "!") }

def pair: int -> (int, int) = { n -> (inc(n) + (match n | 0 -> 0 | m -> m * 10), 7) }

def main = {
  use Ask(ask = { 21 })
  report(twice())
  report(pair(2).0)
}

main()
"""
    runSource source |> assertRan "n = 42!\nn = 23!\n"

[<Fact>]
let ``Run - a context member named by an operator is synthesized from an attached operator`` () =
    let source =
        normalized """import nanyx/console

context Eq(a) = (`==`: (a, a) -> bool)

type Point = protected (x: int, y: int)
def Point.`==`: Point, Point -> bool = { p, q -> p.x == q.x }
def same: [Eq(a)] a, a -> bool = { x, y -> Eq(a).`==`(x, y) }
def `<+>`: int, int -> int = { a, b -> a * 10 + b }

@entry
def main = {
  println(int.toString(`<+>`(1, 2)))
  def combine = `<+>`
  println(int.toString(combine(3, 4)))
  println(if same(Point(x = 1, y = 2), Point(x = 1, y = 9)) then "same" else "different")
  println(if Point.`==`(Point(x = 1, y = 2), Point(x = 2, y = 2)) then "same" else "different")
}
"""
    runSource source |> assertRan "12\n34\nsame\ndifferent\n"

[<Fact>]
let ``Run - a lambda in a generic function calls the members of the context the function requires, and passes it on`` () =
    let source =
        normalized """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }

def describe: [Show(a)] a -> string = { v -> "<" ++ Show(a).show(v) ++ ">" }
def qualified: [Show(a)] list(a) -> string = { xs -> list.fold(xs, "", { acc, x -> acc ++ Show(a).show(x) }) }
def plain: [Show(a)] list(a) -> string = { xs -> list.fold(xs, "", { acc, x -> acc ++ show(x) }) }
def passed: [Show(a)] list(a) -> string = { xs -> list.fold(xs, "", { acc, x -> acc ++ describe(x) }) }
def nested: [Show(a)] list(a) -> string = { xs ->
  list.fold(xs, "", { acc, x -> acc ++ list.fold([1, 2], "", { inner, n -> inner ++ show(x) }) })
}

@entry
def main = {
  def points = [Point(x = 1, y = 2), Point(x = 3, y = 4)]
  println(qualified(points))
  println(plain(points))
  println(passed(points))
  println(nested([Point(x = 5, y = 6)]))
}
"""
    runSource source |> assertRan "(1, 2)(3, 4)\n(1, 2)(3, 4)\n<(1, 2)><(3, 4)>\n(5, 6)(5, 6)\n"

/// A parameterised context, two types with instances of it, and a function that requires it, for the tests of the
/// contexts a function that does not write its type requires (#544).
let private inferredShowPrelude =
    normalized """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)
type Size = (w: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def Size.show: Size -> string = { s -> "{s.w}px" }
def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
def twice = { v -> describe(v) ++ describe(v) }

"""

[<Fact>]
let ``Run - a function that does not write its type passes on the parameterised context it was inferred to require`` () =
    let source =
        inferredShowPrelude +
        normalized """@entry
def main = { println(twice(Point(x = 1, y = 2))) }
"""
    runSource source |> assertRan "(1, 2)(1, 2)\n"

[<Fact>]
let ``Run - an inferred parameterised context passes through two functions and is used at two types`` () =
    let source =
        inferredShowPrelude +
        normalized """def thrice = { v -> twice(v) ++ describe(v) }
def both = { p, s -> describe(p) ++ " " ++ describe(s) }
def logged = { v -> println(describe(v)) }
def lengths = { xs -> list.map(xs, { x -> describe(x) }) \list.length }

@entry
def main = {
  println(thrice(Point(x = 1, y = 2)))
  println(twice(Size(w = 3)))
  println(both(Point(x = 4, y = 5), Size(w = 6)))
  logged(Size(w = 7))
  println(int.toString(lengths([Size(w = 8), Size(w = 9)])))
}
"""
    runSource source |> assertRan "(1, 2)(1, 2)(1, 2)\n3px3px\n(4, 5) 6px\n7px\n2\n"

/// `Sum`, whose `` `0` `` is a value rather than a function, with an instance for `Point` synthesized from its attached
/// definitions, for the tests of context members used as values (#580).
let private sumPrelude =
    normalized """import nanyx/console

context Sum(a) = (`0`: a, `+`: (a, a) -> a)

type Point = (x: int, y: int)
def Point.`0`: Point = Point(x = 0, y = 0)
def Point.`+`: Point, Point -> Point = { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }

"""

[<Fact>]
let ``Run - a context member that is a value is passed from a synthesized instance`` () =
    let source =
        sumPrelude +
        normalized """def sum: [Sum(a)] list(a) -> a = { items -> items \fold(`0`) { + } }

@entry
def main = {
  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])
  println("{total.x}, {total.y}")
}
"""
    runSource source |> assertRan "9, 12\n"

[<Fact>]
let ``Run - a context member that is a value is named through its context`` () =
    let source =
        sumPrelude +
        normalized """def sum: [Sum(a)] list(a) -> a = { items -> items \fold(Sum(a).`0`) { a, b -> Sum(a).`+`(a, b) } }

@entry
def main = {
  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4)])
  println("{total.x}, {total.y}")
}
"""
    runSource source |> assertRan "4, 6\n"

[<Fact>]
let ``Run - a synthesized instance from attached definitions that do not write their types`` () =
    // `Point.`+`` reads the fields of any record that has them, so it is generic: it is passed at the member's type.
    let source =
        normalized """import nanyx/console

context Sum(a) = (`0`: a, `+`: (a, a) -> a)

type Point = (x: int, y: int)
def Point.`0` = Point(x = 0, y = 0)
def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
def sum: [Sum(a)] list(a) -> a = { items -> items \fold(`0`) { a, b -> `+`(a, b) } }

@entry
def main = {
  def total = sum([Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])
  println("{total.x}, {total.y}")
}
"""
    runSource source |> assertRan "9, 12\n"

[<Fact>]
let ``Run - a function member of no parameters is passed from a synthesized instance`` () =
    let source =
        normalized """import nanyx/console

context Zero(a) = (zero: () -> a)

type Point = (x: int, y: int)
def Point.zero: () -> Point = { Point(x = 7, y = 8) }
def origin: [Zero(a)] () -> a = { zero() }

@entry
def main = {
  def p: Point = origin()
  println("{p.x}, {p.y}")
}
"""
    runSource source |> assertRan "7, 8\n"

[<Fact>]
let ``Run - a function member is passed on as a value, by its name and through its context`` () =
    let source =
        normalized """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def concat: list(string) -> string = { xs -> xs \list.fold("") { acc, s -> acc ++ s } }
def shown: [Show(a)] list(a) -> string = { xs -> concat(list.map(xs, show)) }
def qualified: [Show(a)] list(a) -> string = { xs -> concat(list.map(xs, Show(a).show)) }

@entry
def main = {
  println(shown([Point(x = 1, y = 2), Point(x = 3, y = 4)]))
  println(qualified([Point(x = 5, y = 6)]))
}
"""
    runSource source |> assertRan "(1, 2)(3, 4)\n(5, 6)\n"

[<Fact>]
let ``Run - an operator in a generic function calls the member of the context the function requires`` () =
    let source =
        normalized """import nanyx/console

context Add(a) = (`+`: (a, a) -> a)

type Point = (x: int, y: int)
def Point.`+`: Point, Point -> Point = { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }

def add: [Add(a)] a, a -> a = { x, y -> x + y }
def addCounting: [Add(a)] a, a, int -> (a, int) = { x, y, n -> (x + y, n + 1) }
def sum: [Add(a)] a, list(a) -> a = { start, items -> items \fold(start) { + } }

@entry
def main = {
  def p = add(Point(x = 1, y = 2), Point(x = 3, y = 4))
  println("{p.x} {p.y}")
  def (q, n) = addCounting(p, Point(x = 10, y = 20), 41)
  println("{q.x} {q.y} {n}")
  def total = sum(Point(x = 0, y = 0), [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)])
  println("{total.x} {total.y}")
}
"""
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

[<Fact>]
let ``Run - a lambda calls a member a use loads (#778)`` () =
    let source =
        """import nanyx/console

context Foo = (bar: string -> string)

def calledInUse = {
  use Foo(bar = { s -> s ++ "?" })
  def f = { bar("w") }
  f()
}

println(calledInUse())
"""
    runSource source |> assertRan "w?\n"

[<Fact>]
let ``Run - a lambda passed to list.map calls a member a use loads (#778)`` () =
    let source =
        """import nanyx/console

context Foo = (bar: string -> string)

def mapped = {
  use Foo(bar = { s -> s ++ "!" })
  ["a", "b"] \list.map { s -> bar(s) } \list.fold("", { acc, s -> acc ++ s })
}

println(mapped())
"""
    runSource source |> assertRan "a!b!\n"

[<Fact>]
let ``Run - a nested lambda passes the members a use loads on to a def that requires them (#778)`` () =
    let source =
        """import nanyx/console

context Foo = (bar: string -> string)

def twice: [Foo] string -> string = { s -> bar(bar(s)) }

def nested = {
  use Foo(bar = { s -> s ++ "." })
  def outer = { x ->
    def inner = { twice(x) }
    inner()
  }
  outer("n")
}

println(nested())
"""
    runSource source |> assertRan "n..\n"
