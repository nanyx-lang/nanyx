/// Arithmetic on values the rest of the def settles to `float`, after the operator, is float arithmetic (#1485).
module TranspilerWasmTests.Features.Floats.RunFloatSettledLater

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - operands a later push settles to float are added as floats`` () =
    let source = """def pop = { mut stack ->
  match stack
    | [] -> crash("empty")
    | [value, ...rest] ->
      stack := rest
      value
}

def push = { mut stack, value -> stack := [value] ++ stack }

def calc: () -> float = {
  memory {
    mut s = []
    def addTop = { push(mut s, pop(mut s) + pop(mut s)) }
    push(mut s, 1.75)
    push(mut s, 1.75)
    addTop()
    pop(mut s)
  }
}

println("{calc()}")
"""
    runSource source |> assertRan "3.5\n"
