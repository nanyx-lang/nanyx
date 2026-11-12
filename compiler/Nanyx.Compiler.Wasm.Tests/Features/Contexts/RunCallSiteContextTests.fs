/// A function value uses the context instances in scope where it is called, not where it is made: each call passes it
/// the members of the contexts its type requires (#836).
module TranspilerWasmTests.Features.Contexts.CallSiteInstances

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a closure passed to a function calls the instance of the use around its call`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def run: [c] ([c] () -> string) -> string = { f ->
  use Foo(bar = { s -> "inner " ++ s }) in f()
}

def main: [Foo] () -> string = { run({ bar("x") }) }

use Foo(bar = { s -> "outer " ++ s }) in println(main())
"""
    runSource source |> assertRan "inner x\n"

[<Fact>]
let ``Run - a closure made under one use of a context and called under another uses the second`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def show: [Foo & Console] () -> () = {
  def f = { bar("x") }
  println(f())
  use Foo(bar = { s -> "second " ++ s }) in println(f())
}

use Foo(bar = { s -> "first " ++ s }) in show()
"""
    runSource source |> assertRan "first x\nsecond x\n"

[<Fact>]
let ``Run - closures stored in a list use the instance where each is called`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def show: [Foo & Console] () -> () = {
  def fs = [{ bar("a") }, { bar("b") }]
  for fs | f -> println(f())
  use Foo(bar = { s -> "second " ++ s })
  for fs | f -> println(f())
}

use Foo(bar = { s -> "first " ++ s }) in show()
"""
    runSource source |> assertRan "first a\nfirst b\nsecond a\nsecond b\n"

[<Fact>]
let ``Run - a function that requires a context passed as a value uses the instance where it is called`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def greet: [Foo] string -> string = { s -> bar(s) }

def twice: [c] ([c] string -> string), string -> string = { f, s ->
  use Foo(bar = { t -> "inner " ++ t }) in f(f(s))
}

def main: [Foo & Console] () -> () = { println(twice(greet, "x")) }

use Foo(bar = { s -> "outer " ++ s }) in main()
"""
    runSource source |> assertRan "inner inner x\n"

[<Fact>]
let ``Run - a pure lambda given where a function that requires a context is expected`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def apply: [Foo] ([Foo] string -> string), string -> string = { f, s -> f(s) }

def main: [Foo & Console] () -> () = { println(apply({ s -> s ++ "!" }, "x")) }

use Foo(bar = { s -> s }) in main()
"""
    runSource source |> assertRan "x!\n"

[<Fact>]
let ``Run - a lambda returned out of the use it was made under uses the instance where it is called`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def make: [Foo] string -> ([Foo] () -> string) = { s -> { bar(s) } }

def main = {
  def f = use Foo(bar = { s -> "first " ++ s }) in make("x")
  use Foo(bar = { s -> "second " ++ s }) in println(f())
}

main()
"""
    runSource source |> assertRan "second x\n"

[<Fact>]
let ``Run - a lambda written inside a use and called under another uses the second`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def main = {
  def f = use Foo(bar = { s -> s ++ "?" }) in { bar("x") }
  use Foo(bar = { s -> s ++ "!" }) in println(f())
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "x!\n"

[<Fact>]
let ``Run - a lambda written after a use statement and returned out of its block uses the instance where it is called`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def make = {
  use Foo(bar = { s -> "first " ++ s })
  def f = { bar("x") }
  println(f())
  f
}

def main = {
  def f = make()
  use Foo(bar = { s -> "second " ++ s }) in println(f())
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "first x\nsecond x\n"

[<Fact>]
let ``Run - a function named as a value where its context is not available, and called where it is`` () =
    let source = """import nanyx/console

context Foo = (bar: string -> string)

def needsFoo: [Foo] string -> string = { s -> bar(s) }

def main = {
  def g = needsFoo
  use Foo(bar = { s -> s ++ "!" }) in println(g("x"))
}

main()
"""
    runSource source |> assertRan "x!\n"

[<Fact>]
let ``Run - a use member that prints calls the write in scope where the use is`` () =
    let source = """import nanyx/console

def main = {
  use (write = { s -> write("[" ++ s ++ "]") })
  write("hi")
}

main()
"""
    runSource source |> assertRan "[hi]"
