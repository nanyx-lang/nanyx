module TranspilerWasmTests.Features.StdLib.RunMathSpecialFloat

open Xunit
open TranspilerWasmTestHelpers

// #1487: `math.infinity`, `math.nan`, and `math.isNaN`, `math.isInfinite` and `math.isFinite`.

[<Fact>]
let ``Run - nanyx/math names infinity and NaN`` () =
    runSource
        """
import nanyx/math as math

println("{math.infinity} {-math.infinity} {math.nan}")
println("{math.infinity == 1.0 / 0.0} {math.nan == math.nan} {math.infinity > 1.0e308}")
"""
    |> assertRan "Infinity -Infinity NaN\ntrue false true\n"

[<Fact>]
let ``Run - nanyx/math tells NaN and the infinities from ordinary floats`` () =
    runSource
        """
import nanyx/math as math

def describe: float -> string = { x -> "{math.isNaN(x)} {math.isInfinite(x)} {math.isFinite(x)}" }

println(describe(0.0))
println(describe(1.5))
println(describe(math.infinity))
println(describe(-math.infinity))
println(describe(math.nan))
println(describe(math.sqrt(-1.0)))
println(describe(math.ln(0.0)))
"""
    |> assertRan
        "false false true\nfalse false true\nfalse true false\nfalse true false\ntrue false false\ntrue false false\nfalse true false\n"
