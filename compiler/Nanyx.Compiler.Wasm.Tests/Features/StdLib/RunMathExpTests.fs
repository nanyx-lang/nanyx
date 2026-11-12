module TranspilerWasmTests.Features.StdLib.RunMathExp

open Xunit
open TranspilerWasmTestHelpers

// #822: `math.exp`, `math.ln`, `math.sign`, and the constants `math.pi` and `math.e`.

[<Fact>]
let ``Run - nanyx/math exp and ln are inverses, and exp(1.0) is e`` () =
    let source =
        """import nanyx/console
import nanyx/math as math

println("{math.exp(1.0) == math.e} {math.exp(1.0)} {math.ln(math.e)}")
println("{math.exp(0.0)} {math.ln(1.0)}")
println("{math.exp(2.0)} {math.exp(-1.0)} {math.ln(math.exp(2.0))}")
"""

    runSource source
    |> assertRan "true 2.718281828459045 1.0\n1.0 0.0\n7.38905609893065 0.36787944117144233 2.0\n"

[<Fact>]
let ``Run - nanyx/math ln is NaN below zero and minus infinity at zero`` () =
    let source =
        """import nanyx/console
import nanyx/math as math

println("{math.ln(-1.0)} {math.ln(0.0)}")
"""

    runSource source |> assertRan "NaN -Infinity\n"

[<Fact>]
let ``Run - nanyx/math sign gives -1, 0 or 1 of the argument's type`` () =
    let source =
        """import nanyx/console
import nanyx/math as math

def direction: [Num(a) & Ord(a)] a, a -> a = { from, to -> math.sign(to - from) }

println("{math.sign(-7)} {math.sign(0)} {math.sign(42)}")
println("{math.sign(-2.5)} {math.sign(0.0)} {math.sign(0.5)} {math.sign(-0.0)}")
println("{direction(5, 2)} {direction(1.5, 4.0)}")
"""

    runSource source |> assertRan "-1 0 1\n-1.0 0.0 1.0 0.0\n-1 1.0\n"

[<Fact>]
let ``Run - nanyx/math pi and e`` () =
    let source =
        """import nanyx/console
import nanyx/math as math

def area: float -> float = { r -> math.pi * r * r }

println("{math.pi} {math.e}")
println("{area(2.0)}")
"""

    runSource source |> assertRan "3.141592653589793 2.718281828459045\n12.566370614359172\n"
