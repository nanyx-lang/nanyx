/// Measured values written as text at run time (#2624): in the largest unit they are a whole number of, through a
/// hole, `toString` and `println`.
module TranspilerWasmTests.Features.Units.RunDisplay

open Xunit
open TranspilerWasmTestHelpers

let private time =
    """dimension Time

@baseUnit unit ns: Time
unit us: Time = 1_000ns
unit ms: Time = 1_000us
unit s: Time = 1_000ms
unit min: Time = 60s
unit h: Time = 60min
unit d: Time = 24h

"""

[<Fact>]
let ``Run - each unit of a time is written in the largest unit it is a whole number of`` () =
    let source =
        time + """@entry
def main = {
  println(7ns)
  println(3us)
  println(1500ms)
  println(2s)
  println(120s)
  println(3h)
  println(2d)
  println(-90min)
  println(1_001ms)
  println(0s)
}
"""
    runSource (normalized source) |> assertRan "7ns\n3us\n1500ms\n2s\n2min\n3h\n2d\n-90min\n1001ms\n0ns\n"

[<Fact>]
let ``Run - arithmetic on an int and a float storage, written as text`` () =
    let source =
        time + """def str: [ToString(a)] a -> string = { x -> toString(x) }

@entry
def main = {
  println("{100ms + 200ms} {100ms + 200ms == 300ms}")
  println("{100.0ms + 200.0ms} {100.0ms + 200.0ms == 300.0ms}")
  println("{2.0 * 1.5s} {1s // 3} {0.25s} {16.5ms} {1.5ns} {0.0s}")
  println(str(90s) ++ " " ++ str(1.5s))
}
"""
    runSource (normalized source) |> assertRan "300ms true\n300ms true\n3s 333333333ns 250ms 16500us 1.5ns 0.0ns\n90s 1500ms\n"

[<Fact>]
let ``Run - a generic storage is written in the base unit, and a part of a structure as its own`` () =
    let source =
        time + """def show: [ToString(a)] Time(a) -> string = { t -> "{t}" }

dimension Bytes(I32)

@baseUnit unit B: Bytes
unit KB: Bytes = 1_000B

@entry
def main = {
  println(show(2s))
  println(show(2.5s))
  println("{(2s, 3ms)} {[1s, 2min]} {(wait = 5min)}")
  println("{3KB} {3B}")
}
"""
    runSource (normalized source) |> assertRan "2000000000ns\n2500000000.0ns\n(2s, 3ms) [1s, 2min] (wait = 5min)\n3KB 3B\n"
