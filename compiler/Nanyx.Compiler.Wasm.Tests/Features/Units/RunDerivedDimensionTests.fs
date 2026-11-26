/// Derived dimensions at run time (#2816): a product or quotient of measured values is the product or quotient of their
/// storages, and is written in its parts' base units.
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
            [ "1.0438413361169103e-8m/ns"
              "100m"
              "6.0m*m"
              "5.0e-18m/(ns*ns)"
              "1000.0"
              "Length / Time(1.0438413361169103e-8)"
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
