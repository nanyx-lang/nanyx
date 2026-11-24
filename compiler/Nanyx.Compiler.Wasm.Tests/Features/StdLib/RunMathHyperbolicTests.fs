module TranspilerWasmTests.Features.StdLib.RunMathHyperbolic

open System
open Xunit
open TranspilerWasmTestHelpers

// #2392: `math.sinh`, `cosh`, `tanh`, `asinh`, `acosh`, `atanh`, `hypot` and `cbrt`, written in Nanyx, within one unit in
// the last place of the correctly rounded result.

/// The calls `calls`, each a call in Nanyx and the correctly rounded value of it, run in one program: each value it prints
/// must be within one unit in the last place of that value, or exactly it when that is NaN, zero or infinite.
let private assertWithinUlp =
    assertPrintsFloats (fun expected actual -> abs (actual - expected) <= Math.BitIncrement(abs expected) - abs expected)

[<Fact>]
let ``Run - nanyx/math sinh, cosh and tanh at 0, 1 and -1`` () =
    assertWithinUlp
        [ "math.sinh(0.0)", 0.0
          "math.sinh(1.0)", 1.1752011936438014
          "math.sinh(-1.0)", -1.1752011936438014
          "math.cosh(0.0)", 1.0
          "math.cosh(1.0)", 1.5430806348152437
          "math.cosh(-1.0)", 1.5430806348152437
          "math.tanh(0.0)", 0.0
          "math.tanh(1.0)", 0.7615941559557649
          "math.tanh(-1.0)", -0.7615941559557649
          "math.sinh(3.0)", 10.017874927409903
          "math.cosh(0.5)", 1.1276259652063807
          "math.tanh(2.0)", 0.9640275800758169 ]

[<Fact>]
let ``Run - nanyx/math sinh and tanh keep a small argument, and tanh goes to exactly 1 or -1`` () =
    let source =
        """import nanyx/math as math

println("{math.sinh(1.0e-10) == 1.0e-10} {math.tanh(1.0e-10) == 1.0e-10} {math.sinh(-1.0e-10) == -1.0e-10}")
println("{math.tanh(1000.0)} {math.tanh(-1000.0)} {math.tanh(math.infinity)}")
println("{math.sinh(math.infinity)} {math.cosh(-math.infinity)} {math.sinh(math.nan)}")
"""

    runSource source |> assertRan "true true true\n1.0 -1.0 1.0\nInfinity Infinity NaN\n"

[<Fact>]
let ``Run - nanyx/math asinh, acosh and atanh undo sinh, cosh and tanh`` () =
    assertWithinUlp
        [ "math.asinh(math.sinh(0.1))", 0.1
          "math.asinh(math.sinh(0.5))", 0.5
          "math.asinh(math.sinh(1.0))", 1.0
          "math.asinh(math.sinh(-3.0))", -3.0
          "math.acosh(math.cosh(1.0))", 1.0
          "math.acosh(math.cosh(2.0))", 2.0
          "math.acosh(math.cosh(5.0))", 5.0
          "math.atanh(math.tanh(0.1))", 0.1
          "math.atanh(math.tanh(0.5))", 0.5
          "math.atanh(math.tanh(-1.0))", -1.0 ]

[<Fact>]
let ``Run - nanyx/math asinh, acosh and atanh, and where acosh and atanh are not defined`` () =
    assertWithinUlp
        [ "math.asinh(0.7)", 0.6526665660823557
          "math.asinh(2.5)", 1.6472311463710958
          "math.acosh(1.5)", 0.9624236501192069
          "math.acosh(3.5)", 1.9248473002384139
          "math.acosh(1.0)", 0.0
          "math.atanh(0.4)", 0.42364893019360184
          "math.atanh(0.2)", 0.2027325540540822
          "math.acosh(0.5)", nan
          "math.atanh(2.0)", nan
          "math.atanh(1.0)", infinity
          "math.atanh(-1.0)", -infinity ]

[<Fact>]
let ``Run - nanyx/math hypot neither overflows nor underflows on the way`` () =
    let source =
        """import nanyx/math as math

println("{math.hypot(3.0, 4.0)} {math.hypot(-3.0, 4.0)} {math.hypot(0.0, 0.0)}")
println("{math.isFinite(math.hypot(1.0e200, 1.0e200))} {math.hypot(1.0e-200, 1.0e-200) != 0.0}")
println("{math.hypot(math.infinity, math.nan)} {math.hypot(math.nan, 1.0)}")
"""

    runSource source |> assertRan "5.0 5.0 0.0\ntrue true\nInfinity NaN\n"
    assertWithinUlp
        [ "math.hypot(1.0, 1.0)", Math.Sqrt 2.0
          "math.hypot(1.0, 2.0)", Math.Sqrt 5.0
          "math.hypot(6.0e200, 8.0e200)", Double.Hypot(6.0e200, 8.0e200)
          "math.hypot(6.0e-200, -8.0e-200)", Double.Hypot(6.0e-200, 8.0e-200)
          "math.hypot(1.0e300, 1.0)", 1.0e300 ]

[<Fact>]
let ``Run - nanyx/math cbrt is the real cube root`` () =
    let source =
        """import nanyx/math as math

println("{math.cbrt(27.0)} {math.cbrt(-8.0)} {math.cbrt(0.0)} {math.cbrt(-1000.0)} {math.cbrt(0.001)}")
println("{math.cbrt(math.infinity)} {math.cbrt(-math.infinity)} {math.cbrt(math.nan)}")
"""

    runSource source |> assertRan "3.0 -2.0 0.0 -10.0 0.1\nInfinity -Infinity NaN\n"
    assertWithinUlp [ "math.cbrt(2.0)", 1.2599210498948732; "math.cbrt(-100.0)", -4.641588833612779 ]
