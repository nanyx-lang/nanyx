module TranspilerWasmTests.Features.StdLib.RunMathGcdLcm

open Xunit
open TranspilerWasmTestHelpers

// #2393: `math.gcd` and `math.lcm`.

[<Fact>]
let ``Run - nanyx/math gcd is never negative`` () =
    let source =
        """import nanyx/math as math

println("{math.gcd(12, 18)} {math.gcd(12, -18)} {math.gcd(-12, -18)}")
println("{math.gcd(0, 5)} {math.gcd(5, 0)} {math.gcd(0, 0)} {math.gcd(7, 7)} {math.gcd(17, 5)}")
"""

    runSource source |> assertRan "6 6 6\n5 5 0 7 1\n"

[<Fact>]
let ``Run - nanyx/math lcm is never negative, and zero with a zero`` () =
    let source =
        """import nanyx/math as math

println("{math.lcm(4, 6)} {math.lcm(0, 5)} {math.lcm(-4, 6)} {math.lcm(4, -6)} {math.lcm(7, 7)}")
"""

    runSource source |> assertRan "12 0 12 12 7\n"
