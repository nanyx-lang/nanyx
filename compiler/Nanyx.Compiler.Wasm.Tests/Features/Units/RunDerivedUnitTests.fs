/// Units of a derived dimension at run time (#2858): a number with one is its number of the parts' default storage units,
/// and a value of the dimension is written in its parts' default display units, never its own (#2974).
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
let ``Run - a value of a derived dimension is written in its parts' display units, not its own`` () =
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
    runSource (normalized source) |> assertRan "25.0m/s\n25.027777777777775m/s\n20.0m/s\n10.438413361169102m/s\n90.0\n"

[<Fact>]
let ``Run - a speed is written in metres a second, whichever of its own units are seen`` () =
    let source =
        speed
        + """unit kmh: Speed = 1km / 1hr
unit mps: Speed = 1m / 1s

println(90.0kmh)
println(1.0kmh)
println(90.0kmh + 10.0mps)
"""
    // A sum of the two, worked out with floats that round, is a whole number of metres a second.
    runSource (normalized source) |> assertRan "25.0m/s\n0.2777777777777778m/s\n35.0m/s\n"

[<Fact>]
let ``Run - a derived dimension's units imported from another module make its values`` () =
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
    runFile mainPath |> assertRan "25.0m/s\n26.8224m/s\n"
