module TranspilerWasmTests.Features.StdLib.RunMathTrigonometry

open System
open Xunit
open TranspilerWasmTestHelpers

// #1452: `math.sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `log10` and `log2`, written in Nanyx, against .NET's
// `Math` to within 1e-15 relative error.

/// The calls `calls`, each a call in Nanyx and the value .NET gives for it, run in one program: each value it prints must
/// be within 1e-15 of .NET's, relative to it, or exactly it when that is NaN, zero or infinite.
let private assertClose =
    assertPrintsFloats (fun expected actual -> abs (actual - expected) <= 1e-15 * abs expected)

[<Fact>]
let ``Run - nanyx/math sin, cos and tan of known angles`` () =
    assertClose
        [ "math.sin(math.pi / 6.0)", 0.5
          "math.cos(math.pi)", -1.0
          "math.tan(math.pi / 4.0)", Math.Tan(Math.PI / 4.0)
          "math.sin(0.0)", 0.0
          "math.cos(0.0)", 1.0
          "math.sin(1.0)", Math.Sin 1.0
          "math.cos(-2.5)", Math.Cos -2.5
          "math.tan(1.2)", Math.Tan 1.2
          "math.sin(-4.0)", Math.Sin -4.0 ]

[<Fact>]
let ``Run - nanyx/math sin and cos of large angles stay accurate`` () =
    assertClose
        [ "math.sin(1.0e6)", Math.Sin 1.0e6
          "math.cos(1.0e6)", Math.Cos 1.0e6
          "math.sin(12345.678)", Math.Sin 12345.678
          "math.cos(-98765.4321)", Math.Cos -98765.4321 ]

[<Fact>]
let ``Run - nanyx/math asin, acos and atan`` () =
    assertClose
        [ "math.asin(0.5)", Math.Asin 0.5
          "math.asin(-1.0)", Math.Asin -1.0
          "math.asin(0.99)", Math.Asin 0.99
          "math.asin(2.0)", nan
          "math.acos(0.5)", Math.Acos 0.5
          "math.acos(-1.0)", Math.PI
          "math.acos(0.999)", Math.Acos 0.999
          "math.acos(-1.5)", nan
          "math.atan(1.0)", Math.PI / 4.0
          "math.atan(-0.3)", Math.Atan -0.3
          "math.atan(0.6)", Math.Atan 0.6
          "math.atan(2.0)", Math.Atan 2.0
          "math.atan(1.0e10)", Math.Atan 1.0e10
          "math.atan(math.infinity)", Math.PI / 2.0 ]

[<Fact>]
let ``Run - nanyx/math atan2 takes y first and tells the four quarters apart`` () =
    assertClose
        [ "math.atan2(1.0, -1.0)", 3.0 * Math.PI / 4.0
          "math.atan2(0.0, -1.0)", Math.PI
          "math.atan2(-1.0, -1.0)", -3.0 * Math.PI / 4.0
          "math.atan2(1.0, 1.0)", Math.PI / 4.0
          "math.atan2(-2.0, 3.0)", Math.Atan2(-2.0, 3.0)
          "math.atan2(1.0, 0.0)", Math.PI / 2.0
          "math.atan2(-1.0, 0.0)", -Math.PI / 2.0
          "math.atan2(0.0, 1.0)", 0.0
          "math.atan2(math.nan, 1.0)", nan ]

[<Fact>]
let ``Run - nanyx/math log10 and log2 are exact at powers of their base`` () =
    let source =
        """import nanyx/math as math

println("{math.log10(1000.0) == 3.0} {math.log2(1024.0) == 10.0} {math.log10(0.001) == -3.0} {math.log2(0.125) == -3.0}")
println("{math.log10(0.0)} {math.log2(-1.0)}")
"""

    runSource source |> assertRan "true true true true\n-Infinity NaN\n"
    assertClose [ "math.log10(2.0)", Math.Log10 2.0; "math.log2(10.0)", Math.Log2 10.0; "math.log10(123456.0)", Math.Log10 123456.0 ]
