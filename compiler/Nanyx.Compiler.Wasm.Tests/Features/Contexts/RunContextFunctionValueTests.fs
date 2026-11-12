/// A top-level function that requires contexts, passed as a value where they are available: a closure over the
/// members found where it is named (#802).
module TranspilerWasmTests.Features.Contexts.FunctionValues

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - functions that require Console passed as values inside a Console def`` () =
    let source = """import nanyx/console

def say: [Console] string -> () = { s -> println(s) }

def apply: (string -> ()), string -> () = { f, s -> f(s) }

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

def apply: (string -> ()), string -> () = { f, s -> f(s) }

apply(say, "top")
"""
    runSource source |> assertRan "top\n"

[<Fact>]
let ``Run - println passed as a value at int finds ToString of int`` () =
    let source = """import nanyx/console

def apply: (a -> ()), a -> () = { f, x -> f(x) }

apply(println, 7)
apply(println, "seven")
"""
    runSource source |> assertRan "7\nseven\n"

[<Fact>]
let ``Run - println passed as a value at a type variable gets the ToString its def requires`` () =
    let source = """import nanyx/console

def apply: (a -> ()), a -> () = { f, x -> f(x) }

def show: [ToString(a) + Console] a -> () = { x -> apply(println, x) }

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

def mapOpt: #some(a) | #none, (a -> b) -> #some(b) | #none = { o, f ->
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

def apply: (string -> ()), string -> () = { f, s -> f(s) }

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

context Greeter = (greet: string -> ())

def hello: [Greeter] string -> () = { name -> greet(name) }

def apply: (string -> ()), string -> () = { f, s -> f(s) }

@entry
def main = {
  use Greeter(greet = { name -> println("Hello, " ++ name) })
  apply(hello, "Nanyx")
}
"""
    runSource source |> assertRan "Hello, Nanyx\n"
