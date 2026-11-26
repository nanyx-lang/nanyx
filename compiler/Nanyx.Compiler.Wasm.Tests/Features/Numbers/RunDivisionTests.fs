module TranspilerWasmTests.Features.Numbers.RunDivision

open Xunit
open TranspilerWasmTestHelpers

// #2716: `/` divides with a fraction, to a `float` on the integer types and to an `F32` on `F32`; `//` divides and rounds
// down, and `%` takes what is left, with the sign of the right operand, so that `x == (x // y) * y + x % y`.

[<Fact>]
let ``Run - / on ints gives a float, and // and % are floored`` () =
    let source =
        """def n = list.length([1, 2, 3, 4, 5])
println("{7 / 2} {n / 2} {1 / 3} {-7 / 2} {1 / 0} {-1 / 0}")
println("{7 // 2} {-7 // 2} {7 // -2} {-7 // -2} {6 // -3}")
println("{7 % 2} {-7 % 2} {7 % -2} {-7 % -2} {6 % -3}")
println((-4..4) \seq.map({ x -> (x // 3) * 3 + x % 3 == x }) \seq.toList)
println("{9007199254740993 / 1}")
"""

    runSource (normalized source)
    |> assertRan "3.5 2.5 0.3333333333333333 -3.5 Infinity -Infinity\n3 -4 -4 3 -2\n1 1 -1 -1 0\n[true, true, true, true, true, true, true, true, true]\n9007199254740992.0\n"

[<Fact>]
let ``Run - % of x by a positive n is in 0..n-1`` () =
    runSource "println((-10..10) \\seq.map({ x -> x % 3 }) \\seq.toList)\n"
    |> assertRan "[2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1]\n"

[<Fact>]
let ``Run - / on the sized integers gives a float, and // and % are floored`` () =
    let source =
        """def a: I8 = -7
def b: I8 = 2
def c: I32 = -7
def d: I32 = -2
def u: U8 = 7
def v: U8 = 2
def w: U64 = 18000000000000000000
def x: U64 = 7
println("{a / b} {a // b} {a % b} {c / d} {c // d} {c % d}")
println("{u / v} {u // v} {u % v} {w // x} {w % x}")
"""

    runSource (normalized source) |> assertRan "-3.5 -4 1 3.5 3 -1\n3.5 3 1 2571428571428571428 4\n"

[<Fact>]
let ``Run - // and % on floats and F32s are Python's`` () =
    let source =
        """def f: F32 = -7.5
def g: F32 = 2.0
println("{-7.5 // 2.0} {-7.5 % 2.0} {7.5 // 2.0} {7.5 % 2.0} {7.5 // -2.0} {7.5 % -2.0}")
println("{f / g} {f // g} {f % g} {1.0 // 0.0} {-1.0 % 1.0} {1.0 % -1.0}")
println("{-1.5 % 1.0} {5.5 % -2.0} {-7.5 // 1.0e308} {-7.5 % 1.0e308}")
"""

    runSource (normalized source)
    |> assertRan "-4.0 0.5 3.0 1.5 -4.0 -0.5\n-3.75 -4.0 0.5 Infinity 0.0 -0.0\n0.5 -0.5 -1.0 1.0e308\n"

[<Fact>]
let ``Run - a generic function divides to its type's fraction`` () =
    let source =
        """def mean: [Num(a)] list(a) -> fraction = { xs -> list.sum(xs) / fromInt(xs.length) }
def ratio = { x, y -> x / y }
def parts = { x, y -> (x // y, x % y) }
def xs: list(F32) = [1.0, 2.0]
println("{mean([1, 2, 4])} {mean([1.0, 2.0])} {mean(xs)}")
println("{ratio(7, 2)} {ratio(-7.5, 2.0)} {parts(-7, 2)} {parts(-7.5, 2.0)}")
"""

    runSource (normalized source) |> assertRan "2.3333333333333335 1.5 1.5\n3.5 -3.75 (-4, 1) (-4.0, 0.5)\n"

[<Fact>]
let ``Run - a negative power of an integer says to divide for the fraction`` () =
    let outcome = runSource "println(2 ** -1)\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(
        "crash: -1 is a negative exponent, and an integer raised to one isn't an integer: for the fraction, write 1 / x ** 1\n",
        withoutStack outcome.StandardError)
    runSource "println(\"{2 ** 10} {2.0 ** -1} {1 / 2 ** 1}\")\n" |> assertRan "1024 0.5 0.5\n"

[<Fact>]
let ``Run - a type whose fraction is another has its powers, and stops on a negative one`` () =
    let source =
        """type Count = protected (n: int)

def Count.zero: Count = Count(n = 0)
def Count.`+`: Count, Count -> Count = { a, b -> Count(n = a.n + b.n) }
def Count.`-`: Count, Count -> Count = { a, b -> Count(n = a.n - b.n) }
def Count.negate: Count -> Count = { a -> Count(n = -a.n) }
def Count.`*`: Count, Count -> Count = { a, b -> Count(n = a.n * b.n) }
def Count.`/`: Count, Count -> float = { a, b -> a.n / b.n }
def Count.`//`: Count, Count -> Count = { a, b -> Count(n = a.n // b.n) }
def Count.`%`: Count, Count -> Count = { a, b -> Count(n = a.n % b.n) }
def Count.fromInt: int -> Count = { n -> Count(n = n) }
def Count.isInteger: bool = false

def x = Count(n = 2)
def minusOne = list.length([0]) - 2
println("{(x ** 3).n} {Count.fromInt(1) / x ** 1}")
println((x ** minusOne).n)
"""

    let outcome = runSource (normalized source)
    Assert.Equal("8 0.5\n", outcome.StandardOutput)
    Assert.Equal(1, outcome.ExitCode)
    Assert.StartsWith("crash: a negative power of a ", withoutStack outcome.StandardError)

[<Fact>]
let ``Run - a division of literals passed straight to println prints its fraction`` () =
    runSource (normalized "println(7 / 2)\nprintln(1s / 1ms)\nprintln(1s // 1ms)\n") |> assertRan "3.5\n1000.0\n1000\n"
