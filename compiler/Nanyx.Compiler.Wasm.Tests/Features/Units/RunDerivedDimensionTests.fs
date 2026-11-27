/// Derived dimensions at run time (#2816): a product or quotient of measured values is the product or quotient of their
/// storages, and is written in its parts' default display units.
module TranspilerWasmTests.Features.Units.RunDerivedDimension

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a speed is a length over a time, and back again`` () =
    let source =
        """dimension Length

@baseUnit unit m: Length
unit km: Length = 1_000m

dimension Speed = Length / Time

def speed: Length(float), Time(float) -> Speed(float) = { d, t -> d / t }

def v = speed(100.0m, 9.58s)
println(v)
println(v * 9.58s)
println(2.0m * 3.0m)
println(10.0m / 2.0s / 1.0s)
def ratio: float = 1.0km / 1.0m
println(ratio)
dbg(v)
"""
    runSource source
    |> withoutDbgLocations
    |> assertRan (
        String.concat
            "\n"
            [ "10.438413361169102m/s"
              "100.0m"
              "6.0m*m"
              "5.0m/(s*s)"
              "1000.0"
              "(Length / Time)(1.0438413361169103e-8)"
              "" ])

// The result's type is left to inference, and was the def's own fraction at the call, which no instance fixed, so the
// quotient was printed as `()cm/s` (#2879).
[<Fact>]
let ``Run - a quotient of a generic storage whose result type is inferred`` () =
    let source =
        """dimension Distance

@baseUnit unit cm: Distance

dimension Time

@baseUnit unit s: Time

def speed: Distance(a), Time(a) -> _ = { d, t -> d / t }

println(speed(100cm, 2s))
println(speed(100.0cm, 4.0s))
"""
    runSource source |> assertRan "50.0cm/s\n25.0cm/s\n"

// The parts of the result's type both name the storage's fraction, which was read as two storages (#2879).
[<Fact>]
let ``Run - a quotient whose parts are stored as a storage's fraction`` () =
    let source =
        """dimension Distance

@baseUnit unit cm: Distance

dimension Time

@baseUnit unit s: Time

def speed: Distance(a), Time(a) -> Distance(Num(a).fraction) / Time(Num(a).fraction) = { d, t -> d / t }

println(speed(100cm, 2s))
"""
    runSource source |> assertRan "50.0cm/s\n"

// The dimensions a function is passed written without their storage share one, so a quotient of them, its result left
// to the body, is of that storage's fraction (#2918, #2879).
[<Fact>]
let ``Run - a quotient of dimensions written without their storage`` () =
    let source =
        """dimension Distance

@baseUnit unit cm: Distance

dimension Time

@baseUnit unit s: Time

def speed: Distance, Time -> _ = { d, t -> d / t }

def pace: Distance, Time -> Distance / Time = { d, t -> d / t }

println(speed(100cm, 2s))
println(pace(100.0cm, 4.0s))
"""
    runSource source |> assertRan "50.0cm/s\n25.0cm/s\n"

/// `Distance`, stored in millimetres and displayed in metres.
let private distance =
    """dimension Distance

unit mm: Distance
@baseUnit unit m: Distance = 1_000mm
unit km: Distance = 1_000m

dimension Speed = Distance / Time

unit kmh: Speed = 1km / 1hr

"""

[<Fact>]
let ``Run - a derived value is written in its parts' display units, never its own`` () =
    let source =
        distance + """@entry
def main = {
  println(1m / 1s)
  println(100.0m / 9.58s)
  println(90.0kmh)
  println(1500mm * 1mm)
  println(2000mm * 1000mm)
  println(1.0m / 1.0s / 1.0s)
  dbg(1m / 1s)
}
"""
    runSource (normalized source)
    |> assertRanWithDbg
        "1.0m/s\n10.438413361169102m/s\n25.0m/s\n1500mm*mm\n2m*m\n1.0m/(s*s)\n"
        "[main.nyx:19] (Distance / Time)(0.000001)\n"

[<Fact>]
let ``Run - dbg writes a derived dimension in brackets around its number, and a single one without`` () =
    // `Distance / Time(1.0)` would read as `Distance` over `Time(1.0)` (#2970).
    let source =
        normalized """dimension Distance
@baseUnit unit m: Distance
dimension Time
@baseUnit unit s: Time

(1m / 1s) \dbg
(1m * 2m) \dbg
(1m) \dbg
"""
    runSource source |> assertRanWithDbg "" "[main.nyx:6] (Distance / Time)(1.0)\n[main.nyx:7] (Distance * Distance)(2)\n[main.nyx:8] Distance(1)\n"
