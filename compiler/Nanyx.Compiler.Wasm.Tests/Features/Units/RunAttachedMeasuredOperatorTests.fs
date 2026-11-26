/// An operator attached to a type of its own, beside a measured value, at run time (#2873).
module TranspilerWasmTests.Features.Units.RunAttachedMeasuredOperator

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an operator a type attaches that takes a time is called and gives its result`` () =
    let source =
        """type Stamp = private int

def Stamp.`+`: Stamp, Time(int) -> Stamp = { Stamp(a), t -> Stamp(a + t // 1ns) }

def Stamp.`-`: Stamp, Stamp -> Time(int) = { Stamp(a), Stamp(b) -> (a - b) * 1ns }

def Stamp.nanos: Stamp -> int = { Stamp(a) -> a }

type Offset = private int

def Offset.`+`: Offset, Time(int) -> Time(int) = { Offset(n), t -> t + n * 1s }

def s = Stamp(5_000_000_000)
println((s + 3s) \nanos)
println((s + 3s) - s)
println(Offset(2) + 3s)
println(2 * 3s)
"""
    runSource (normalized source) |> assertRan "8000000000\n3s\n5s\n6s\n"
