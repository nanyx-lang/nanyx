module TranspilerWasmTests.Features.Functions.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a lambda's parameters may be patterns that take its argument apart`` () =
    let source =
        normalized """import nanyx/console

type $Email = string

def address: $Email -> string = { $Email(s) -> s }
def weigh = { (a, b), c -> a + b * c }
def answer = { () -> 42 }
def middle = { (), (_, (m, _)) -> m }

@entry
def main = {
  println(address($Email("ada@example.com")))
  println("{weigh((1, 2), 3)} {answer()} {middle((), (0, ("mid", 1)))}")
  def sums = [(1, 2), (30, 40)] \map { (x, y) -> x + y }
  for sums | n -> println("{n}")
  def offset = 100
  def shifted = [(1, 2)] \map { (x, y) ->
    def total = x + y
    total + offset
  }
  for shifted | n -> println("{n}")
}
"""
    runSource source |> assertRan "ada@example.com\n7 42 mid\n3\n70\n103\n"

[<Fact>]
let ``Run - local defs in a pattern lambda's arms`` () =
    let source =
        normalized """import nanyx/console

def f: [Console] list(int) -> () = {
  | [] ->
    def done = "end"
    println(done)
  | [n, ...rest] ->
    def label = "x"
    def doubled = n * 2
    println(label ++ "{doubled}")
    f(rest)
}

def g: [Console] (int, string) -> () = {
  | 0, s ->
    def last = s ++ "!"
    println(last)
  | n, s ->
    def next = s ++ "."
    g(n - 1, next)
}

@entry
def main = {
  f([1, 2])
  g(3, "go")
}
"""
    runSource source |> assertRan "x2\nx4\nend\ngo...!\n"

[<Fact>]
let ``Run - a pipe passes its value in front of several arguments`` () =
    let source =
        normalized """import nanyx/console

def digits: int, int, int -> int = { a, b, c -> a * 100 + b * 10 + c }
def digitsThen: int, int, int, (int -> int) -> int = { a, b, c, h -> h(digits(a, b, c)) }

println("{1 \digits(2, 3)}")
println("{1 \digitsThen(2, 3) { + 1000 }}")
"""
    runSource source |> assertRan "123\n1123\n"

[<Fact>]
let ``Run - shorthand lambdas filter, map and fold a list`` () =
    // `{ % 2 == 0 }` is `{ x -> x % 2 == 0 }`: the body goes on after the operation on the left-out argument.
    let source =
        normalized """import nanyx/console

def items = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
def x = 100

def items2 = items \filter { % 2 == 0 }

for items2 | i -> println("{i}")
for items \filter { > 8 } \map { * x + 1 } | i -> println("{i}")
println("{items \fold(0) { + }}")
println("{["a", "", "bc"] \filter { != "" } \map { .length } \fold(0) { + }}")
"""
    runSource source |> assertRan "2\n4\n6\n8\n10\n901\n1001\n55\n3\n"

[<Fact>]
let ``Run - a shorthand lambda compares a field of its argument`` () =
    // `{ .age > 18 }` is `{ p -> p.age > 18 }`: the field access is the left operand of what follows it.
    let source =
        normalized """import nanyx/console

type P = (name: string, age: int)

def ps: list(P) = [P(name = "a", age = 12), P(name = "b", age = 30), P(name = "c", age = 40)]

println("{ps \list.filter { .name == "b" } \list.length}")
for ps \list.filter { .age > 18 } | p -> println(p.name)
println("{ps \list.filter { .age * 2 >= 60 } \list.length}")
"""
    runSource source |> assertRan "1\nb\nc\n2\n"

[<Fact>]
let ``Run - a function takes positional and named arguments together, called or piped into`` () =
    let source =
        normalized """import nanyx/console

def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }
def digits: (int, int, last: int) -> int = { a, b, last -> a * 100 + b * 10 + last }

println("{stepped(1, step = 2)}")
println("{1 \stepped(step = 2)}")
println("{digits(1, 2, last = 3)}")
println("{1 \digits(2, last = 3)}")
"""
    runSource source |> assertRan "12\n12\n123\n123\n"

[<Fact>]
let ``Run - multi-argument pattern lambdas`` () =
    // The two-parameter `{ | [], _ -> ... | [head, ...tail], f -> ... }` form from the pattern-matching page (#45).
    let source =
        normalized """import nanyx/console

def map: (list(int), (int -> int)) -> list(int) = {
  | [], _ -> []
  | [head, ...tail], f -> [f(head), ...map(tail, f)]
}
def sum: list(int) -> int = {
  | [] -> 0
  | [head, ...tail] -> head + sum(tail)
}
@entry
def main = {
  println("{sum(map([1, 2, 3], { * 10 }))}")
}
"""
    runSource source |> assertRan "60\n"

[<Fact>]
let ``Run - an annotated zero-parameter function returning a tag union`` () =
    // The `() -> ...` annotation names a unit input for a lambda with no parameters; the backend used to
    // crash with a negative padding count instead of compiling it.
    let source =
        normalized """import nanyx/console

def getMessage: () -> #some(string) | #noMessage = {
  def message = "Hello, from Nanyx!"
  #some(message)
}

@entry
def main = {
  match getMessage()
    | #some(_) -> println("There was a message")
    | #noMessage -> println("No message available")
}
"""
    runSource source |> assertRan "There was a message\n"

[<Fact>]
let ``Run - a function annotated as returning a function returns a closure`` () =
    // `int -> (int -> int)` is one parameter returning a function; the backend used to crash with a
    // negative padding count on the annotation, then reported closures as unsupported (#29).
    let source =
        normalized """import nanyx/console

def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }

@entry
def main = {
  def add5 = makeAdder(5)
  dbg(add5(10))
}
"""
    runSource source |> assertRan "dbg: 15\n"

[<Fact>]
let ``Run - a lambda argument's inferred parameter types`` () =
    // A lambda passed to a call is lifted to a function of its own; its parameters' types come from the typed
    // call it was an argument of, so `p.x` has a record layout and `"{n}"` an interpolation type.
    let source =
        normalized """import nanyx/console

type P = (x: int)
def apply: P, (P -> int) -> int = { p, f -> f(p) }
def mapSome
  : (#some(a) | r), (a -> b) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }

@entry
def main = {
  def n = P(x = 3) \apply { p -> p.x }
  def x: #some(int) | #notFound = #some(n)
  def m = x \mapSome { v -> "got {v}" }
  match m
    | #some(s) -> println(s)
    | #notFound -> println("none")
}
"""
    runSource source |> assertRan "got 3\n"

[<Fact>]
let ``Run - calling a unit function whose lambda takes a parameter`` () =
    // `{ _ -> ... }` has one parameter, the unit, which `getData()` passes.
    let source =
        normalized """import nanyx/console

def getData: () -> #some(int) | #notFound = { _ -> #some(42) }

@entry
def main = {
  match getData()
    | #some(n) -> println("got {n}")
    | #notFound -> println("none")
}
"""
    runSource source |> assertRan "got 42\n"

[<Fact>]
let ``Run - mutually recursive top-level functions`` () =
    // Regression: a def could not call one declared later in the file (#47).
    let source =
        normalized """import nanyx/console

def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }
def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }
def show: [Console] bool -> () = { b -> println(if b then "yes" else "no") }
show(isEven(10))
show(isOdd(7))
show(isEven(7))
dbg(later(2))
def later = { x -> x * 21 }
"""
    runSource source |> assertRan "yes\nyes\nno\ndbg: 42\n"

[<Fact>]
let ``Run - functions as values and higher-order calls`` () =
    // #28: a parameter holding a function can be called; a named function or a lambda can be passed.
    let source =
        normalized """import nanyx/console

def apply: ((int -> int), int) -> int = { f, x -> f(x) }
def twice: ((int -> int), int) -> int = { f, x -> f(f(x)) }
def double: int -> int = { x -> x * 2 }
def map: (list(int), (int -> int)) -> list(int) = { xs, f ->
  match xs
    | [] -> []
    | [head, ...tail] -> [f(head)] ++ map(tail, f)
}
def sum: list(int) -> int = { xs ->
  match xs
    | [] -> 0
    | [head, ...tail] -> head + sum(tail)
}
def main = {
  def square = { x -> x * x }
  dbg(apply(double, 21))
  dbg(apply({ x -> x + 1 }, 41))
  dbg(apply({ * 3 }, 14))
  dbg(twice(double, 10))
  dbg(square(6))
  dbg(sum(map([1, 2, 3], double)))
  dbg(sum([1, 2, 3] \map { * 10 }))
}
main()
"""
    runSource source |> assertRan "dbg: 42\ndbg: 42\ndbg: 42\ndbg: 40\ndbg: 36\ndbg: 12\ndbg: 60\n"

[<Fact>]
let ``Run - an attached function passed as a value`` () =
    // `Point.norm` as an argument is read as a member access on `Point`, not as the name of a function.
    let source =
        normalized """import nanyx/console

type Point = (x: int, y: int)
def Point.norm: Point -> int = { p -> p.x + p.y }
def apply: ((Point -> int), Point) -> int = { f, p -> f(p) }
dbg(apply(Point.norm, (x = 3, y = 4)))
dbg(list.fold([(x = 1, y = 2), (x = 10, y = 20)] \list.map(Point.norm), 0, { a, b -> a + b }))
"""
    runSource source |> assertRan "dbg: 7\ndbg: 33\n"

[<Fact>]
let ``Run - a capturing lambda passed to a function`` () =
    let source =
        normalized """def apply = { f, x -> f(x) }
def main = { n ->
  apply({ x -> x + n }, 1)
}
dbg(main(2))
"""
    runSource source |> assertRan "dbg: 3\n"

[<Fact>]
let ``Run - closures`` () =
    // Closures capture locals, parameters and other closures; captured strings and records keep their types.
    let source =
        normalized """import nanyx/console

def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }
def twice: ((int -> int), int) -> int = { f, v -> f(f(v)) }
def greeter: string -> (string -> string) = { greeting -> { name -> greeting ++ ", " ++ name } }
type Point = (x: int, y: int)
@entry
def main = {
  def add5 = makeAdder(5)
  println("{add5(10)} {twice(add5, 1)} {twice(makeAdder(1), 0)}")
  def hello = greeter("Hello")
  println(hello("Ada"))
  def origin = Point(x = 1, y = 2)
  def shiftX = { d -> origin.x + d }
  def compose = { g -> { v -> add5(g(v)) } }
  def shiftedThenAdd5 = compose(shiftX)
  def doubledThenAdd5 = compose({ v -> v * 2 })
  println("{shiftX(10)}")
  dbg(shiftedThenAdd5(1))
  dbg(doubledThenAdd5(3))
  def limit = 3
  rec countTo = { i -> if i < limit then countTo(i + 1) else i }
  println("{countTo(0)}")
}
"""
    runSource source |> assertRan "15 11 2\nHello, Ada\n11\ndbg: 7\ndbg: 11\n3\n"

[<Fact>]
let ``Run - a function that only calls the closure it is given compiles without making one`` () =
    let source =
        normalized """def apply: (int -> int), int -> int = { f, x -> f(x) }

@entry
def main = { }
"""
    runSource source |> assertRan ""

[<Fact>]
let ``Run - a generic function specialised at two types gives each its own result`` () =
    // `orElse` is specialised for ints and for strings, and `main` calls both specialisations (see CoreIrTests.fs).
    let source =
        normalized """import nanyx/console

def orElse = { o, fallback ->
  match o
    | #some(v) -> v
    | #none -> fallback
}

def pick = { n -> if n > 0 then #some(n) else #none }

def name = { n -> if n > 0 then #some("positive") else #none }

def describe = { n -> "{n * 1}" }

def main = {
  println(describe(orElse(pick(5), 0)))
  println(describe(orElse(pick(-5), 0)))
  println(orElse(name(1), "none"))
  println(orElse(name(0), "none"))
}

main()
"""
    runSource source |> assertRan "5\n0\npositive\nnone\n"

[<Fact>]
let ``Run - closures through the core IR`` () =
    // `compose`'s lambda is specialised along with `compose` (see CoreIrTests.fs).
    let source =
        normalized """import nanyx/console

def apply = { f, x -> f(x) }

def double: int -> int = { n -> n * 2 }

def compose = { f, g -> { x -> g(f(x)) } }

def countdown: int -> int = { n ->
  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + i) }
  loop(n, 0)
}

def show = { n -> "{n * 1}" }

def main = {
  def offset = 10
  def both = compose(double, { x -> x + offset })
  println(show(apply({ x -> x + offset }, 1)))
  println(show(apply(double, 4)))
  println(show(both(1)))
  println(apply({ s -> s ++ "!" }, "hi"))
  println(show(countdown(4)))
}

main()
"""
    runSource source |> assertRan "11\n8\n12\nhi!\n10\n"

[<Fact>]
let ``Run - holes in annotations are filled in by inference`` () =
    let source =
        normalized """import nanyx/console

def add: (int, _) -> _ = { a, b -> a + b }
def names: list(_) = ["ada", "grace"]
def id: _ -> _ = { x -> x }
def greet = { (name: _) -> "hi {name}" }

@entry
def main = {
  def total: _ = add(40, 2)
  println("{total} {id(7)} {id("same")}")
  for names | n -> println(greet(n))
}
"""
    runSource source |> assertRan "42 7 same\nhi ada\nhi grace\n"

[<Fact>]
let ``Run - an annotated lambda parameter before a body that starts with a name (#272)`` () =
    let source =
        """
import nanyx/console

type Point = (x: int, y: int)
def f = { q: Point -> q }
def apply = { g: (int -> int), n: int -> g(n) }

@entry
def main = {
  def p = f((x = 1, y = 2))
  println("{p.x + p.y} {apply({ n -> n * 10 }, 4)}")
}
"""
    runSource source |> assertRan "3 40\n"

[<Fact>]
let ``Run - a local lambda of no parameters runs each time it is called (#589)`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def x = 10
  def block = {
    def a = 3
    println("ran")
    a + x
  }
  println("before")
  println("{block()}")
  println("{block()}")
  def nothing = {}
  nothing()
}
"""
    runSource source |> assertRan "before\nran\n13\nran\n13\n"

[<Fact>]
let ``Run - a lambda of no parameters can be passed as a value (#589)`` () =
    let source =
        normalized """import nanyx/console

def twice: (() -> int) -> int = { f -> f() + f() }

@entry
def main = {
  def x = 10
  def next = { x + 1 }
  println("{twice(next)} {twice({ x * 2 })}")
  def thunks = [{ x }, { x - 1 }]
  for thunks | t -> println("{t()}")
}
"""
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
  println("{mapSomeOr(#none, 7, { n -> n + 1 })}")
}
"""
    runSource source |> assertRan "7 8\ndbg: 2\n0 2\n7\n"

[<Fact>]
let ``Run - a lambda of no parameters checked against a one-parameter type ignores the parameter (#813)`` () =
    let source = """import nanyx/console

def apply: (int -> string) -> string = { f -> f(1) }
def top: int -> string = { "top" }
def run: (() -> a) -> a = { f -> f() }

@entry
def main = {
  def suffix = "!"
  def local: int -> string = { "local" }
  println(apply { "ignored" })
  println(apply { "captured{suffix}" })
  println("{top(5)} {local(6)} {apply(top)} {apply(local)}")
  println("{run { 1 }} {run { () -> 2 }}")
  println("{[1, 2, 3] \list.map { 0 }}")
}
"""
    runSource source |> assertRan "ignored\ncaptured!\ntop local top local\n1 2\n[0, 0, 0]\n"

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
        normalized """def makeAdder = { n -> { m -> n + m } }
def adder = makeAdder(dbg(1))
dbg(adder(2))
"""
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
