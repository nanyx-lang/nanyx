module TranspilerWasmTests.Features.Functions.RunUnitFunctionValue

open Xunit
open TranspilerWasmTestHelpers

// A function value of type `() -> a` takes nothing, however its lambda is written, `{ _ -> ... }`, `{ () -> ... }` or
// `{ ... }`, and however it is called, `f()` or `f(())` (#1084).

[<Fact>]
let ``Run - a function value of unit is called the same way however its lambda and the call are written`` () =
    let source =
        """
import nanyx/console

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
import nanyx/console

def run: [Console] (() -> int) -> int = { f -> f(println("before")) }

@entry
def main = { println("{run({ _ -> 1 })}") }
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "before\n1\n"
