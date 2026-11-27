/// A dimension's default storage unit, which a measured value is stored as a number of and `dbg` writes, and its
/// default display unit, which zero is written in (#2971).
module TranspilerWasmTests.Features.Units.RunStorageAndDisplayUnits

open Xunit
open TranspilerWasmTestHelpers

let private distance (marked: string) =
    $"""dimension Distance

{(if marked = "mm" then "@baseUnit " else "")}unit mm: Distance
{(if marked = "m" then "@baseUnit " else "")}unit m: Distance = 1_000mm
{(if marked = "km" then "@baseUnit " else "")}unit km: Distance = 1_000m

"""

[<Fact>]
let ``Run - a value is stored in millimetres whichever unit @baseUnit marks, and zero is written in that unit`` () =
    for marked in [ "mm"; "m"; "km" ] do
        let source =
            distance marked + """@entry
def main = {
  println(0m)
  println(1500mm)
  dbg(2m)
}
"""
        runSource (normalized source) |> assertRanWithDbg $"0{marked}\n1500mm\n" "[main.nyx:11] Distance(2000)\n"

[<Fact>]
let ``Run - a dimension with no @baseUnit is written in its default storage unit`` () =
    let source =
        """dimension Count

unit items: Count
unit dozen: Count = 12items

@entry
def main = {
  println(0items)
  println(24items)
  println(25items)
}
"""
    runSource (normalized source) |> assertRan "0items\n2dozen\n25items\n"

[<Fact>]
let ``Run - a made-up storage unit's value that fits no unit is written as a decimal of the smallest it is one of`` () =
    let source =
        """dimension Distance

@baseUnit unit m: Distance = 1_000mm
unit inch: Distance = 25.4mm
unit mm: Distance

@entry
def main = {
  def float: Distance(float) -> Distance(float) = { d -> d }
  dbg(1inch)
  println("{1inch} {1mm} {2.6mm} {1inch + 1mm} {-2.6mm} {2m} {0mm}")
  println("{float(1.5m)} {float(1inch)} {float(0mm)}")
}
"""
    runSource (normalized source) |> assertRanWithDbg "1inch 1mm 2.6mm 26.4mm -2.6mm 2m 0m\n1.5m 1.0inch 0.0m\n" "[main.nyx:10] Distance(127)\n"

[<Fact>]
let ``Run - a made-up storage unit with no @baseUnit is displayed in the smallest unit it is a decimal of`` () =
    let source =
        """dimension Distance

unit mm: Distance
unit inch: Distance = 25.4mm

@entry
def main = {
  println("{0mm} {0.4mm} {2inch}")
}
"""
    runSource (normalized source) |> assertRan "0mm 0.4mm 2inch\n"

[<Fact>]
let ``Run - the prelude's Time is stored in nanoseconds and written in seconds when it is zero`` () =
    let source =
        """@entry
def main = {
  def none: Time(float) = 0s
  println(0s)
  println(0ms)
  println(none)
  println(1500ms)
  dbg(500ms)
}
"""
    runSource (normalized source) |> assertRanWithDbg "0s\n0s\n0.0s\n1500ms\n" "[main.nyx:8] Time(500000000)\n"
