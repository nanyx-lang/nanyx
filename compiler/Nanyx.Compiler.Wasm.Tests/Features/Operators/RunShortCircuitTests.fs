module TranspilerWasmTests.Features.Operators.ShortCircuit

open Xunit
open TranspilerWasmTestHelpers

// `a and b` evaluates `b` only when `a` is true, and `a or b` only when `a` is false, so a guard on the left protects
// what is on the right (#2479). Where the right operand has no effect, it is evaluated anyway, as `i32.and` or `i32.or`.

let private guarded =
    normalized """def safe: int -> bool = { x -> x != 0 and 10 / x > 1 }
def unsafe: int -> bool = { x -> x == 0 or 10 / x > 1 }

@entry
def main = {
  println(safe(0))
  println(unsafe(0))
  println(safe(5))
  println(unsafe(20))
}
"""

[<Fact>]
let ``Short circuit - and and or don't evaluate their right operand when the left one decides`` () =
    runSource guarded |> assertRan "false\ntrue\ntrue\nfalse\n"

[<Fact>]
let ``Short circuit - and and or don't evaluate their right operand when the left one decides, without inlining`` () =
    runSourceWithOptions true false "" guarded |> assertRan "false\ntrue\ntrue\nfalse\n"

[<Fact>]
let ``Short circuit - a right operand that can trap is evaluated in a branch`` () =
    let wat = transpileWat (normalized "def safe: int -> bool = { x -> x != 0 and 10 / x > 1 }\n")
    let safe = functionText "safe" wat
    Assert.Equal(1, countOccurrences "(if" safe)
    Assert.Equal(0, countOccurrences "i32.and" safe)

[<Fact>]
let ``Short circuit - a right operand with no effect is evaluated anyway, with i32.and or i32.or`` () =
    let wat =
        transpileWat (
            normalized
                """def both: int, int -> bool = { x, y -> x > 0 and y < 5 }
def either: int, int -> bool = { x, y -> x > 0 or y < 5 }
"""
        )
    let both = functionText "both" wat
    Assert.Equal(0, countOccurrences "(if" both)
    Assert.Equal(1, countOccurrences "i32.and" both)
    let either = functionText "either" wat
    Assert.Equal(0, countOccurrences "(if" either)
    Assert.Equal(1, countOccurrences "i32.or" either)
