/// A top-level function that requires contexts, passed as a value where they are available: a closure over the
/// members found where it is named (#802).
module TranspilerWasmTests.Features.Contexts.FunctionValues

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - functions that require Console passed as values inside a Console def`` () =
    let source = """import nanyx/console

def say: [Console] string -> () = { s -> println(s) }

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
    let source = """import nanyx/console

def say: [Console] string -> () = { s -> println(s) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

apply(say, "top")
"""
    runSource source |> assertRan "top\n"

[<Fact>]
let ``Run - println passed as a value at int finds ToString of int`` () =
    let source = """import nanyx/console

def apply: [c] ([c] a -> ()), a -> () = { f, x -> f(x) }

apply(println, 7)
apply(println, "seven")
"""
    runSource source |> assertRan "7\nseven\n"

[<Fact>]
let ``Run - println passed as a value at a type variable gets the ToString its def requires`` () =
    let source = """import nanyx/console

def apply: [c] ([c] a -> ()), a -> () = { f, x -> f(x) }

def show: [ToString(a) & Console] a -> () = { x -> apply(println, x) }

show(5)
"""
    runSource source |> assertRan "5\n"

[<Fact>]
let ``Run - println mapped over a list at the top level`` () =
    let source = """import nanyx/console

[1, 2] \list.map(println)
"""
    runSource source |> assertRan "1\n2\n"

[<Fact>]
let ``Run - println passed to a function that maps an option`` () =
    let source = """import nanyx/console

def mapOpt: [c] #some(a) | #none, ([c] a -> b) -> #some(b) | #none = { o, f ->
  match o
    | #some(x) -> #some(f(x))
    | #none -> #none
}

#some(42) \mapOpt(println)
"""
    runSource source |> assertRan "42\n"

[<Fact>]
let ``Run - a function passed as a value captures the members of the use where it is named`` () =
    let source = """import nanyx/console

def say: [Console] string -> () = { s -> write(s) }

def apply: [c] ([c] string -> ()), string -> () = { f, s -> f(s) }

def main = {
  use (write = { s -> write("[" ++ s ++ "]") })
  apply(say, "quiet")
}

main()
apply(say, "loud")
"""
    runSource source |> assertRan "[quiet]loud"

[<Fact>]
let ``Run - a function that requires a context of the module's own passed as a value`` () =
    let source = """import nanyx/console

context Greeter = (greet: [Console] string -> ())

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
import nanyx/console

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
import nanyx/console
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
import nanyx/console
import nanyx/math

def largest: list(int) -> int = { xs -> xs \fold(0, max) }

println(largest([3, 9, 4]))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "9\n"

[<Fact>]
let ``Run - a context-generic function taken as a value passes a continuation that requires no contexts to a body that requires some`` () =
    // The adapter for `each` taken at no contexts gave the body its `next` unadapted, and the body called it with
    // Console's members: an indirect call type mismatch.
    let source = """import nanyx/console

def each: [c] list(int), ([c] int, ([c] () -> int) -> int), ([c] () -> int) -> int = {
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
