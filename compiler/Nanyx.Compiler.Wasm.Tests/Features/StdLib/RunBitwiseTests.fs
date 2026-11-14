module TranspilerWasmTests.Features.StdLib.RunBitwise

open Xunit
open TranspilerWasmTestHelpers

// #1259: `int.bitAnd`, `bitOr`, `bitXor`, `bitNot` and the shifts.

[<Fact>]
let ``Run - the bitwise functions on ints combine and flip bits, negative ones too`` () =
    let source =
        normalized """import nanyx/console

println("{int.bitAnd(12, 10)} {int.bitOr(12, 10)} {int.bitXor(12, 10)}")
println("{int.bitNot(0)} {int.bitNot(5)} {int.bitAnd(-1, 255)} {int.bitOr(-16, 3)} {int.bitXor(-1, 1)}")
"""
    runSource source |> assertRan "8 14 6\n-1 -6 255 -13 -2\n"

[<Fact>]
let ``Run - a shift keeps the sign or fills with zeros, and takes its count modulo 64`` () =
    let source =
        normalized """import nanyx/console

println("{1 \int.shiftLeft(4)} {int.shiftLeft(1, 63)} {int.shiftLeft(3, 64)} {int.shiftLeft(1, 65)}")
println("{int.shiftRight(-8, 1)} {int.shiftRight(16, 2)} {int.shiftRight(-1, 63)}")
println("{int.shiftRightUnsigned(-1, 60)} {int.shiftRightUnsigned(16, 2)} {int.shiftRightUnsigned(-8, 1)}")
"""
    runSource source
    |> assertRan "16 -9223372036854775808 3 2\n-4 4 -1\n15 4 9223372036854775804\n"

[<Fact>]
let ``Run - a bitwise function is a value, as a monoid's combine`` () =
    // The bitwise monoids of `examples/traitsAndTypeclasses.nyx`.
    let source =
        normalized """import nanyx/console

def andAll = { xs: list(int) -> xs \list.fold(-1, { x, y -> int.bitAnd(x, y) }) }
def orAll = { xs: list(int) -> xs \list.fold(0, int.bitOr) }

println("{andAll([15, 6, 7])} {orAll([1, 2, 8])}")
"""
    runSource source |> assertRan "6 11\n"
