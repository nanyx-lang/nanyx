module TranspilerWasmTests.Features.Operators.ShortCircuit

open Xunit
open TranspilerWasmTestHelpers

// `a and b` evaluates `b` only when `a` is true, and `a or b` only when `a` is false, so a guard on the left protects
// what is on the right (#2479). Where the right operand has no effect, it is evaluated anyway, as `i32.and` or `i32.or`.

let private guarded =
    normalized """def safe: int -> bool = { x -> x != 0 and 10 // x > 1 }
def unsafe: int -> bool = { x -> x == 0 or 10 // x > 1 }

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
    let wat = transpileWat (normalized "def safe: int -> bool = { x -> x != 0 and 10 // x > 1 }\n")
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

// A small function on the right is inlined into the branch, as it would be in an `if`'s, wherever the `and` or `or` is:
// a condition, an argument, a binding (#2486).

[<Fact>]
let ``Short circuit - a small function on the right is inlined into the branch`` () =
    let wat =
        transpileWat (
            normalized
                """def condition: int, string -> int = { x, s -> if x > 0 and string.isEmpty(s) then 1 else 2 }
def argument: int, string -> list(bool) = { x, s -> [x > 0 or string.isEmpty(s)] }
def bound: int, string -> int = { x, s ->
  def b = x > 0 and string.isEmpty(s)
  if b then 1 else 2
}
"""
        )
    for name in [ "condition"; "argument"; "bound" ] do
        let text = functionText name wat
        Assert.Equal(0, countOccurrences "call $string_isEmpty" text)

let private inlinedRight =
    normalized """def count: list(string) -> int = { xs -> list.length(xs) }
def both: list(string), int, string -> list(bool) = { xs, x, s -> [count(xs) > 1, x != 0 and 10 // x > 1 and string.isEmpty(s)] }

@entry
def main = {
  println(both(["a"], 0, ""))
  println(both(["a", "b"], 5, ""))
  println(both(["a", "b"], 5, "c"))
}
"""

[<Fact>]
let ``Short circuit - a right operand inlined into its branch runs only when needed, after what comes before it`` () =
    runSource inlinedRight |> assertRan "[false, false]\n[true, true]\n[true, false]\n"

[<Fact>]
let ``Short circuit - a right operand inlined into its branch runs only when needed, without inlining`` () =
    runSourceWithOptions true false "" inlinedRight |> assertRan "[false, false]\n[true, true]\n[true, false]\n"
