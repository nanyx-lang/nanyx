/// Arithmetic on measured values at run time (#2799): each is the arithmetic on its storage, in base units.
module TranspilerWasmTests.Features.Units.RunArithmetic

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - arithmetic on measured values, at concrete and generic storages`` () =
    let source =
        normalized """dimension Time

@baseUnit unit ns: Time
unit ms: Time = 1_000_000ns
unit s: Time = 1_000ms

def sum: [Add(a)] a, a -> a = { x, y -> x + y }
def half: Time(a) -> Time(fraction) = { t -> t / 2 }
def halfDown: Time(a) -> Time(a) = { t -> t // 2 }
def later: Time(a) -> Time(a) = { t -> t + 5ms }

dbg(100ms + 200ms)
dbg(1s - 300ms)
dbg(2 * 300ms)
dbg(1s / 2)
dbg(1s // 3)
dbg(1s / 1ms)
dbg(1500ms // 1s)
dbg(-1500ms % 1s)
dbg(-(250ms))
dbg(sum(100ms, 200ms))
dbg(half(3ms))
dbg(halfDown(3ms))
dbg(later(3ms))
dbg(1s == 1_000ms)
dbg(1s < 2s)
"""
    runSource source
    |> withoutDbgLocations
    |> assertRan (
        String.concat
            "\n"
            [ "Time(300000000)"; "Time(700000000)"; "Time(600000000)"; "Time(500000000.0)"; "Time(333333333)"; "1000.0"; "1"
              "Time(500000000)"; "Time(-250000000)"; "Time(300000000)"; "Time(1500000.0)"; "Time(1500000)"; "Time(8000000)"
              "true"; "true"; "" ])
