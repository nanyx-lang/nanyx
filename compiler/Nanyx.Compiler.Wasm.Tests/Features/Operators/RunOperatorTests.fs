module TranspilerWasmTests.Features.Operators.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - float arithmetic and printing`` () =
    // Regression: float literals parsed but `pi * 2.0` was "Type mismatch: float vs int" and the backend had
    // no f64 support at all (#36). Floats are boxed f64 values.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def half: float -> float = { x -> x / 2.0 }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def pi = 3.14\n" +
        "  def d = pi * 2.0\n" +
        "  println(\"{d}\")\n" +
        "  println(\"{2.0 * 2.0} {0.0 - 1.5} {1.0 / 3.0} {half(5.0)} {1234567.25 + 0.0}\")\n" +
        "  println(if 2.5 > 1.0 and 1.5 <= 1.5 and 0.1 + 0.2 != 0.3 then \"cmp ok\" else \"cmp wrong\")\n" +
        "  dbg(1.5 + 2.25)\n" +
        "}\n"
    runSource source |> assertRan "6.28\n4.0 -1.5 0.333333 2.5 1234567.25\ncmp ok\ndbg: 3.75\n"

[<Fact>]
let ``Run - the remainder of floats is truncated, with the sign of the left operand`` () =
    // #607: `%` on floats type-checked and then had no instruction in the backend. It is C's `fmod`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def neg = 0.0 - 7.5\n" +
        "  println(\"{7.5 % 2.0} {neg % 2.0} {7.5 % (0.0 - 2.0)} {6.0 % 2.0} {1.5 % 4.0}\")\n" +
        "  println(\"{7 % 3} {(0 - 7) % 3}\")\n" +
        "}\n"
    runSource source |> assertRan "1.5 -1.5 1.5 0.0 1.5\n1 -1\n"

[<Fact>]
let ``Run - an operator on a type that attaches it calls the attached definition`` () =
    // #527: `` def $V.`+` = ... `` parsed, but `a + b` on a `$V` was the built-in `+` and a type mismatch.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type $V = (x: int, y: int)\n" +
        "def $V.`+`: $V, $V -> $V = { p, q -> $V(x = p.x + q.x, y = p.y + q.y) }\n" +
        "def $V.`==`: $V, $V -> bool = { p, q -> p.x == q.x }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def v = $V(x = 1, y = 2) + $V(x = 3, y = 4)\n" +
        "  println(\"{v.x} {v.y}\")\n" +
        "  println(if v == $V(x = 4, y = 0) then \"same x\" else \"different x\")\n" +
        "  println(if v != $V(x = 4, y = 0) then \"different x\" else \"same x\")\n" +
        "}\n"
    runSource source |> assertRan "4 6\nsame x\nsame x\n"

[<Fact>]
let ``Run - boolean keywords not, and, or`` () =
    // #39: the docs make boolean operations keywords; `not` had no spelling at all.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "def between: (int, int, int) -> bool = { x, lo, hi -> x >= lo and x <= hi }\n" +
        "show(not true)\n" +
        "show(not false)\n" +
        "show(true and false)\n" +
        "show(true or false)\n" +
        "show(not (1 > 2) and between(5, 1, 10))\n" +
        "show(not between(50, 1, 10) or false)\n"
    runSource source |> assertRan "no\nyes\nno\nyes\nyes\nyes\n"
