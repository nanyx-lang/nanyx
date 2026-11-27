/// Numbers with units at run time (#2798): each is its number of its dimension's default storage units, in the storage
/// it is typed with.
module TranspilerWasmTests.Features.Units.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a number with a unit is its number of default storage units, in its storage`` () =
    let source =
        normalized """dimension Time

@baseUnit unit ns: Time
unit us: Time = 1_000ns
unit ms: Time = 1_000us
unit s: Time = 1_000ms

dimension Length(float)

@baseUnit unit m: Length
unit mm: Length = 0.001m

dimension Bytes(int)

@baseUnit unit B: Bytes
unit KB: Bytes = 1_000B

def expected: Time(float) = 500ms
def whole: Time(int) = 0.5s

dbg(0.5s)
dbg(500ms)
dbg(expected)
dbg(whole)
dbg(-1.5e3us)
dbg(5mm)
dbg(50.5KB)
"""
    runSource source
    |> withoutDbgLocations
    |> assertRan "Time(500000000)\nTime(500000000)\nTime(500000000.0)\nTime(500000000)\nTime(-1500000)\nLength(5.0)\nBytes(50500)\n"
