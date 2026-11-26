/// Units of a derived dimension at run time (#2858): a number with one is its number of the parts' base units, and a
/// value of the dimension is written in the largest of its units the module sees that it is a whole number of.
module TranspilerWasmTests.Features.Units.RunDerivedUnit

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// `Length`, with metres and kilometres, and `Speed`, a length over a time.
let private speed =
    """dimension Length

@baseUnit unit m: Length
unit km: Length = 1_000m

dimension Speed = Length / Time
"""

[<Fact>]
let ``Run - a value of a derived dimension is written in its own units`` () =
    let source =
        speed
        + """unit kmh: Speed = 1km / 1hr

def fast: Speed(float) = 90.0kmh
println(fast)
println(fast + 100.0m / 1.0hr)
println(36.0km / 0.5hr)
println(100.0m / 9.58s)
println(fast / 1.0kmh)
"""
    // 90.1 kilometres an hour, and a speed that is no whole number of them, are written in the parts' base units.
    runSource (normalized source) |> assertRan "90kmh\n2.5027777777777776e-8m/ns\n72kmh\n1.0438413361169103e-8m/ns\n90.0\n"

[<Fact>]
let ``Run - the largest unit a speed is a whole number of is the one it is written in`` () =
    let source =
        speed
        + """unit kmh: Speed = 1km / 1hr
unit mps: Speed = 1m / 1s

println(90.0kmh)
println(1.0kmh)
println(90.0kmh + 10.0mps)
"""
    // A metre a second is the larger unit: 90 kilometres an hour is 25 of them, and a sum of the two, worked out with
    // floats that round, is written in it as well.
    runSource (normalized source) |> assertRan "25mps\n1kmh\n35mps\n"

[<Fact>]
let ``Run - a derived dimension's units imported from another module write its values`` () =
    let lib =
        """export dimension Length

@baseUnit export unit m: Length
export unit km: Length = 1_000m

export dimension Speed = Length / Time

export unit kmh: Speed = 1km / 1hr
"""
    let main =
        """import speeds

unit mph: Speed = 1.609344kmh

println(90.0kmh)
println(60.0mph)
"""
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "speeds.nyx"), normalized lib)
    let mainPath = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(mainPath, normalized main)
    runFile mainPath |> assertRan "90kmh\n60mph\n"
