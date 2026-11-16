module TranspilerWasmTests.Features.Functions.RunCallResult

open Xunit
open TranspilerWasmTestHelpers

// A call on a call's result, `f()(args)`, calls the result (#1076).

[<Fact>]
let ``Run - a call on a call's result calls it, in a def's value, a statement and an interpolation hole`` () =
    let source =
        """
def g = { { 42 } }

def add = { a -> { b -> a + b } }

def mk = { { println("x") } }

@entry
def main = {
  def h = g()()
  println("{h}")
  mk()()
  println("{g()()} {add(1)(2)} {add(1)(2) + add(3)(4)}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "42\nx\n42 3 10\n"

[<Fact>]
let ``Run - calls on call results nest`` () =
    let source =
        """
def add3 = { a -> { b -> { c -> a * 100 + b * 10 + c } } }

@entry
def main = {
  def k = { add3(1)(2)(3) }
  println("{k()}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "123\n"

[<Fact>]
let ``Run - a returned lambda's parameter takes its type from the def's annotation`` () =
    // #1749
    let source =
        """
import nanyx/text

def f: string -> string -> int = { a -> { b -> b \length } }

println(f("x")("yy"))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n"
