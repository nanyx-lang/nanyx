module TranspilerWasmTests.Features.Functions.RunUnitFunctionValue

open Xunit
open TranspilerWasmTestHelpers

// A function value of type `() -> a` takes nothing, however its lambda is written, `{ _ -> ... }`, `{ () -> ... }` or
// `{ ... }`, and however it is called, `f()` or `f(())` (#1084).

[<Fact>]
let ``Run - a function value of unit is called the same way however its lambda and the call are written`` () =
    let source =
        """
def run: (() -> int) -> int = { rest -> rest() + rest(()) }

def wrapped: () -> int = { _ -> 100 }

@entry
def main = {
  println("{run({ _ -> 1 })} {run({ 2 })} {run({ () -> 3 })} {run(wrapped)}")
  def k = { _ -> println("k") }
  k()
  k(())
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2 4 6 200\nk\nk\n"

[<Fact>]
let ``Run - an argument of unit that does something is still worked out`` () =
    let source =
        """
def run: [Console] (() -> int) -> int = { f -> f(println("before")) }

@entry
def main = { println("{run({ _ -> 1 })}") }
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "before\n1\n"

[<Fact>]
let ``Run - a call of unit beside another call in a tuple compiles, its argument a name or a call`` () =
    // The argument is not passed, but what it does is done as part of the call, which waits to be computed where the
    // tuple is made, after the other call: a store beside it broke the backend's own rule (#2662).
    let source =
        """
def names: (int -> list(int)), (() -> list(int)), int, () -> (list(int), list(int)) = { f, g, x, u -> (f(x), g(u)) }

def calls
  : [Console] ([Console] int -> list(int)), ([Console] () -> list(int)), int -> (list(int), list(int))
  = { f, g, x -> (f(x), g(println("second"))) }

@entry
def main = {
  println(names({ n -> [n] }, { () -> [7] }, 3, ()))
  println(calls({ n -> println("first"); [n] }, { () -> println("third"); [7] }, 3))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([3], [7])\nfirst\nsecond\nthird\n([3], [7])\n"
