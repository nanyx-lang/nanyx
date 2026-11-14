module TranspilerWasmTests.Features.ControlFlow.BoolMatch

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - matching a function value's bool result against true and false picks the right arm`` () =
    let source =
        normalized """import nanyx/console

def same: int, int -> bool = { a, b -> a == b }

def f: int, int, (int, int -> bool) -> bool = { x, y, cmp ->
  match cmp(x, y)
    | true -> true
    | false -> false
}

@entry
def main = {
  println("{f(1, 1, same)}")
  println("{f(1, 2, same)}")
}
"""
    runSource source |> assertRan "true\nfalse\n"
