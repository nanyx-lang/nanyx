module TranspilerWasmTests.Features.StdLib.RunMathModGcdLcm

open Xunit
open TranspilerWasmTestHelpers

// #2393: `math.mod`, the modulo with the sign of the divisor, and `math.gcd` and `math.lcm`.

[<Fact>]
let ``Run - nanyx/math mod takes the sign of the divisor`` () =
    let source =
        """import nanyx/math as math

println("{math.mod(7, 3)} {math.mod(-7, 3)} {math.mod(7, -3)} {math.mod(-7, -3)}")
println("{math.mod(6, 3)} {math.mod(-6, 3)} {math.mod(0, -3)}")
println("{math.mod(-1.5, 1.0)} {math.mod(5.5, -2.0)}")
def x: I32 = -7
def n: I32 = 3
println("{math.mod(x, n)}")
"""

    runSource source |> assertRan "1 2 -2 -1\n0 0 0\n0.5 -0.5\n2\n"

[<Fact>]
let ``Run - nanyx/math mod of x by a positive n is in 0..n-1`` () =
    let source =
        """import nanyx/math as math

println((-10..10) \seq.map({ x -> math.mod(x, 3) }) \seq.toList)
"""

    runSource source
    |> assertRan "[2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1]\n"

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
