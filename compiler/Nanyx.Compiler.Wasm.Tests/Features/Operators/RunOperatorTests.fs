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
    runSource source |> assertRan "6.28\n4.0 -1.5 0.3333333333333333 2.5 1234567.25\ncmp ok\ndbg: 3.75\n"

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
let ``Run - prefix minus negates ints and floats, and keeps the sign of zero`` () =
    // #609: `-x` did not parse. On a float it is IEEE 754's negation, so `-0.0` is negative zero: 1 / -0.0 is -inf.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x = 5\n" +
        "  def z = 0.0\n" +
        "  println(\"{-x} {-x * 2} {3 - -x} {-(2.5)} {Int.negate(4)} {Float.negate(1.5)}\")\n" +
        "  println(\"{1.0 / -z < 0.0} {1.0 / -(-z) > 0.0} {1.0 / Float.negate(z) < 0.0}\")\n" +
        "}\n"
    runSource source |> assertRan "-5 -10 8 -2.5 -4 -1.5\ntrue true true\n"

[<Fact>]
let ``Run - generic code adds through Add, at ints, floats and a type that attaches its members`` () =
    // #609: `List.sum: [Add(a)] list(a) -> a`, and `0` beside a value of the type variable is `zero`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type $Money = (pence: int)\n" +
        "def $Money.zero: $Money = $Money(pence = 0)\n" +
        "def $Money.`+`: $Money, $Money -> $Money = { a, b -> $Money(pence = a.pence + b.pence) }\n" +
        "def $Money.`-`: $Money, $Money -> $Money = { a, b -> $Money(pence = a.pence - b.pence) }\n" +
        "def $Money.negate: $Money -> $Money = { a -> $Money(pence = -a.pence) }\n" +
        "def orZero: [Add(a)] a -> a = { x -> x + 0 }\n" +
        "def twiceLess: [Add(a)] a, a -> a = { x, y -> -(x - y - y) }\n" +
        "def none: list(int) = []\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def m = [$Money(pence = 150), $Money(pence = 250)] \\List.sum\n" +
        "  println(\"{[1, 2, 3] \\List.sum} {[1.5, 2.5] \\List.sum} {List.sum(none)} {m.pence} {(-m).pence}\")\n" +
        "  println(\"{orZero(4)} {orZero(2.5)} {twiceLess(1, 3)} {twiceLess(m, m).pence}\")\n" +
        "}\n"
    runSource source |> assertRan "6 4.0 0 400 -400\n4 2.5 5 400\n"

[<Fact>]
let ``Run - generic code multiplies and divides through Num, with integer literals, at ints and floats`` () =
    // #610: `*`, `/` and `%` read through `Num`, a literal beside a value of the type variable is `fromInt` of it, and
    // a function that requires `Num` passes it to one that requires `Add`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def square: [Num(a)] a -> a = { x -> x * x }\n" +
        "def halfPlusOne: [Num(a)] a -> a = { x -> x / 2 + 1 }\n" +
        "def rem3: [Num(a)] a -> a = { x -> x % 3 }\n" +
        "def mean: [Num(a)] list(a) -> a = { xs -> List.sum(xs) / fromInt(List.length(xs)) }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{square(3)} {square(1.5)} {halfPlusOne(7)} {halfPlusOne(7.0)} {rem3(7)} {rem3(7.5)}\")\n" +
        "  println(\"{mean([1, 2, 4])} {mean([1.0, 2.0, 4.5])} {Float.fromInt(3)} {Int.`*`(6, 7)}\")\n" +
        "}\n"
    runSource source |> assertRan "9 2.25 4 4.5 1 1.5\n2 2.5 3.0 42\n"

[<Fact>]
let ``Run - ** is the prelude's pow, and Float.pow takes any exponent`` () =
    // #611: `**` did not parse. It groups to the right, so `2 ** 3 ** 2` is 2 ** 9.
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def cube: [Num(a)] a -> a = { x -> x ** 3 }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{2 ** 10} {1.5 ** 2} {2 ** 3 ** 2} {(2 ** 3) ** 2} {2 * 3 ** 2} {2 ** -1} {2.0 ** -2}\")\n" +
        "  println(\"{Float.pow(2.0, 0.5)} {Float.pow(2.0, 10.0)} {Float.pow(10.0, -1.5)} {Float.pow(0.0, 0.5)}\")\n" +
        "  println(\"{math.pow(2, 8)} {math.pow(1.5, 3)} {pow(3, 4)} {cube(2)} {cube(0.5)}\")\n" +
        "}\n"
    runSource source |> assertRan "1024 2.25 512 64 18 0 0.25\n1.414213562373095 1024.0 0.0316227766016838 0.0\n256 3.375 81 8 0.125\n"

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
