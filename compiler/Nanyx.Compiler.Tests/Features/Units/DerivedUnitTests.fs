/// Units of a derived dimension (#2858): declared from its parts' units, written as literals, and imported as other units
/// are, with the errors in their declarations.
module Nanyx.Compiler.Tests.Features.DerivedUnits

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `Length`, with metres and kilometres, and `Speed`, a length over a time, with no units yet.
let private speed = "dimension Length\n@baseUnit unit m: Length\nunit km: Length = 1_000m\ndimension Speed = Length / Time\n"

/// The code and the text each diagnostic of compiling `source` after `speed` is reported at, and its message.
let private reported (source: string) =
    let source = withFinalNewline (speed + source)
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic), diagnostic.Message)

[<Fact>]
let ``A unit of a derived dimension is its parts' units multiplied and divided, an exact number of their base units`` () =
    let result = Compiler.compile (withFinalNewline (speed + "unit kmh: Speed = 1km / 1hr\nunit mps: Speed = 1m / 1s\nunit mph: Speed = 1.609344kmh"))
    Assert.Empty(result.Diagnostics)
    let scale name = Rational.text result.Typed.Value.Units[name].Scale
    // A metre a nanosecond is the base: a kilometre an hour is 1_000 of them over 3_600_000_000_000.
    Assert.Equal("1/3600000000", scale "kmh")
    Assert.Equal("1/1000000000", scale "mps")
    // A mile an hour, 1.609344 kilometres an hour, is 1_609_344 metres over 3_600_000_000_000_000 nanoseconds.
    Assert.Equal("1397/3125000000000", scale "mph")

[<Fact>]
let ``A number with a unit of a derived dimension is a value of it`` () =
    let result = Compiler.compile (withFinalNewline (speed + "unit kmh: Speed = 1km / 1hr\ndef fast = 90.0kmh\ndef run = 100.0m / 9.58s\ndef faster = fast + run"))
    Assert.Empty(result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types[name]
    Assert.Equal(typeOf "run", typeOf "fast")
    Assert.Equal(typeOf "run", typeOf "faster")

[<Fact>]
let ``A whole number of a derived unit that is no whole number of the base units can't be an int`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.TypeMismatch.Code,
          "90kmh",
          "'90kmh' is 1/40000000 of a 'm/ns', which is no whole number of 'm/ns', so it can't be stored as 'int': write it as a decimal, `90.0kmh`, to store it as a `float`" ],
        reported "unit kmh: Speed = 1km / 1hr\ndef slow: Speed(int) = 90kmh")

[<Fact>]
let ``A unit of a derived dimension that its parts' units don't define is NYX0297`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.UnitOfDerivedDimension.Code,
          "unit loose: Speed",
          "'Speed' is a derived dimension, whose units are multiples of its parts' default storage units: define 'loose' from its parts' units, as in `unit kmh: Speed = 1km / 1hr`" ],
        reported "unit loose: Speed")
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.UnitOfDerivedDimension.Code,
          "unit base: Speed",
          "'Speed' is a derived dimension, whose values are stored in its parts' default storage units and displayed in their default display units, so it has no default storage or display unit of its own: define 'base' from its parts' units, as in `unit kmh: Speed = 1km / 1hr`" ],
        reported "@baseUnit unit base: Speed")

[<Fact>]
let ``A unit of a derived dimension defined from units of another dimension is NYX0289`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.UnitOfAnotherDimension.Code,
          "1km / 1s / 1s",
          "'accel' is a unit of 'Speed', which is 'Length / Time', but `1km / 1s / 1s` is a 'Length / (Time * Time)': define it from units of 'Length / Time'"
          DiagnosticCodes.UnitOfAnotherDimension.Code,
          "1km",
          "'far' is a unit of 'Speed', which is 'Length / Time', but `1km` is a 'Length': define it from units of 'Length / Time'" ],
        reported "unit accel: Speed = 1km / 1s / 1s\nunit far: Speed = 1km")

[<Fact>]
let ``A unit worth nothing, or divided by nothing, is NYX0298`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.UnitWorthNothing.Code, "0s", "`0s` is nothing, so 'instant' would be no quantity at all: define it from a number of units that isn't zero"
          DiagnosticCodes.UnitWorthNothing.Code, "0m", "`0m` is nothing, so 'none' would be no quantity at all: define it from a number of units that isn't zero" ],
        reported "unit instant: Speed = 1m / 0s\nunit none: Length = 0m")

/// A module that exports `Length`, `Speed` and a unit of each.
let private speedModule =
    "module mod\nexport dimension Length\n@baseUnit export unit m: Length\nexport unit km: Length = 1_000m\nexport dimension Speed = Length / Time\nexport unit kmh: Speed = 1km / 1hr\n"

[<Fact>]
let ``An import brings in a derived dimension's units, and a module defines more from them`` () =
    let main = "import mod\nunit mph: Speed = 1.609344kmh\ndef fast: Speed(float) = 90.0kmh\ndef faster: Speed(float) = 60.0mph\n"
    let result = compileImporting speedModule main
    Assert.Empty(result.Diagnostics)
    Assert.Equal("1/3600000000", Rational.text result.Typed.Value.Units["kmh"].Scale)
    Assert.Equal("1397/3125000000000", Rational.text result.Typed.Value.Units["mph"].Scale)
