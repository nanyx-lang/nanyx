/// A top-level function that requires contexts, passed as a value where they are available: a closure over the
/// members found where it is named (#802).
module TranspilerWasmTests.Features.Contexts.FunctionValues

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - functions that require Console passed as values inside a Console def`` () =
    let source = """def say: [Console] string -> () = { s -> println(s) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

def main: [Console] () -> () = {
  apply(say, "inside a Console def")
  [1, 2] \list.map(println)
  ()
}

main()
"""
    runSource source |> assertRan "inside a Console def\n1\n2\n"

[<Fact>]
let ``Run - a function that requires Console passed as a value at the top level`` () =
    let source = """def say: [Console] string -> () = { s -> println(s) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

apply(say, "top")
"""
    runSource source |> assertRan "top\n"

[<Fact>]
let ``Run - a def that writes its contexts, defined point-free`` () =
    let source = """def main: [Console] () -> () = {
  def h: [Console] string -> () = println
  g(3)
  k(4)
  h("five")
}

def g: [Console] int -> () = println

def say: [Console] int -> () = { x -> println(x) }

def k: [Console] int -> () = say

main()
"""
    runSource source |> assertRan "3\n4\nfive\n"

[<Fact>]
let ``Run - println passed as a value at int finds ToString of int`` () =
    let source = """def apply: [c] ([c] a -> ()), a -> () = { f, x -> f(x) }

apply(println, 7)
apply(println, "seven")
"""
    runSource source |> assertRan "7\nseven\n"

[<Fact>]
let ``Run - println passed as a value at a type variable gets the ToString its def requires`` () =
    let source = """def apply: [c] ([c] a -> ()), a -> () = { f, x -> f(x) }

def show: [ToString(a) & Console] a -> () = { x -> apply(println, x) }

show(5)
"""
    runSource source |> assertRan "5\n"

[<Fact>]
let ``Run - println mapped over a list at the top level`` () =
    let source = """[1, 2] \list.map(println)
"""
    runSource source |> assertRan "1\n2\n"

[<Fact>]
let ``Run - println passed to a function that maps an option`` () =
    let source = """def mapOpt: [c] #some(a) | #none, ([c] a -> b) -> #some(b) | #none = { o, f ->
  match o
    | #some(x) -> #some(f(x))
    | #none -> #none
}

#some(42) \mapOpt(println)
"""
    runSource source |> assertRan "42\n"

[<Fact>]
let ``Run - a function passed as a value captures the members of the use where it is named`` () =
    let source = """def say: [Console] string -> () = { s -> write(s) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

def main = {
  use Console(
    write = { s -> write("[" ++ s ++ "]") }
    writeError = writeError
    readLine = readLine
    readToEnd = readToEnd
    flush = flush
  )
  apply(say, "quiet")
}

main()
apply(say, "loud")
"""
    runSource source |> assertRan "[quiet]loud"

[<Fact>]
let ``Run - a function that requires a context of the module's own passed as a value`` () =
    let source = """context Greeter = (greet: [Console] string -> ())

def hello: [Greeter & Console] string -> () = { name -> greet(name) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

@entry
def main = {
  use Greeter(greet = { name -> println("Hello, " ++ name) })
  apply(hello, "Nanyx")
}
"""
    runSource source |> assertRan "Hello, Nanyx\n"

[<Fact>]
let ``Run - a lambda calls the instance a use inside it loads, and the one in scope where it is called before that`` () =
    let source =
        """
context Log = (log: string -> ())

def greet: [Log] string -> () = { name -> log("hello {name}") }

def nested = {
  { use Log(log = { m -> println("inner " ++ m) }) in greet("a") }
}

def before: [Log & Console] () -> ([Log & Console] () -> ()) = {
  {
    greet("b")
    use Log(log = { m -> println("inner " ++ m) })
    greet("c")
  }
}

@entry
def main = {
  def run = nested()
  run()
  use Log(log = { m -> println("outer " ++ m) })
  def later = before()
  later()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "inner hello a\nouter hello b\ninner hello c\n"

[<Fact>]
let ``Run - a function that requires Ord, named through a module alias, passed to fold`` () =
    // `math.max` was named as a member access, which found no instance of `Ord(int)`: the backend looked for one in
    // scope and found none (#1136).
    let source =
        """
import nanyx/math as math

def largest: list(int) -> int = { xs -> xs \fold(0, math.max) }

def largestOf: list(int) -> int = { xs -> list.fold(xs, 0, math.max) }

println(largest([3, 9, 4]))
println(largestOf([3, 9, 4]))
println([3, -9, 4] \list.map(math.abs) \fold(0) { a, b -> a + b })
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "9\n9\n16\n"

[<Fact>]
let ``Run - a function that requires Ord, imported unqualified, passed to fold`` () =
    let source =
        """
import nanyx/math

def largest: list(int) -> int = { xs -> xs \fold(0, max) }

println(largest([3, 9, 4]))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "9\n"

[<Fact>]
let ``Run - a context-generic function taken as a value passes a continuation that requires no contexts to a body that requires some`` () =
    // The adapter for `each` taken at no contexts gave the body its `next` unadapted, and the body called it with
    // Console's members: an indirect call type mismatch.
    let source = """def each: [c] list(int), ([c] int, ([c] () -> int) -> int), ([c] () -> int) -> int = {
  | [], _, rest -> rest()
  | [item, ...others], body, rest -> body(item, { each(others, body, rest) })
}

@entry
def main = {
  def f = each
  def r = f([1, 2], { item, next ->
    println("{item}")
    next()
  }, { 7 })
  println("{r}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1\n2\n7\n"

let private twice = """context Show(a) = (show: a -> string)
def describe: [Show(a)] a -> string = { v -> show(v) }
def twice = { v -> describe(v) ++ describe(v) }
"""

[<Fact>]
let ``Run - a function that requires a parameterised context named as a value at a type nothing constrains`` () =
    let source = twice + """@entry
def main = {
  dbg(twice)
}
"""
    runSource (normalized source) |> assertRan "dbg: <function () -> string>\n"

[<Fact>]
let ``Run - a local def whose value is such a function, never called`` () =
    let source = twice + """@entry
def main = {
  def g = twice
  println("ok")
}
"""
    runSource (normalized source) |> assertRan "ok\n"


[<Fact>]
let ``Run - a local def whose value is a function that requires a parameterised context, called (#807)`` () =
    let source = """def main: [Console] () -> () = {
  def f = println
  f(3)
}

main()
"""
    runSource (normalized source) |> assertRan "3\n"

[<Fact>]
let ``Run - a point-free local def called at two types, and one of two parameters`` () =
    let source = twice + """def both: [Show(a)] a, a -> string = { x, y -> describe(x) ++ "&" ++ describe(y) }
type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
@entry
def main = {
  def f = println
  f(3)
  f("hi")
  def b = both
  f(b(Point(x = 1, y = 2), Point(x = 3, y = 4)))
}
"""
    runSource (normalized source) |> assertRan "3\nhi\n(1, 2)&(3, 4)\n"

[<Fact>]
let ``Run - a top-level def whose value names such a function point-free (#1389)`` () =
    let source = "" + twice + """type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def q = println
def d = describe
def t = twice
def viaAnnotated: [Console] () -> () = { q(t(Point(x = 1, y = 2))) }
def viaInferred = { q(7) }
q(1.5)
q(d(Point(x = 3, y = 4)))
viaAnnotated()
viaInferred()
"""
    runSource (normalized source) |> assertRan "1.5\n(3, 4)\n(1, 2)(1, 2)\n7\n"