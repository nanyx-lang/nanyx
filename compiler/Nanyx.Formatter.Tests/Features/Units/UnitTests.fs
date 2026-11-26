/// Units of measure: unit literals as written, and dimension and unit declarations.
module Nanyx.Formatter.Tests.Features.Units.Units

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A unit literal is written as it is, joined to its number`` () =
    unchanged (lines [ "def a = 500ms"; "def b = 1_000ns"; "def c = 0.5s"; "def d = 1.5e3ms"; "def e = -500ms"; "def f = sleep(500ms) + 2 * 1s" ])

[<Fact>]
let ``A negated unit literal keeps its brackets`` () =
    unchanged (lines [ "def a = -(500ms)" ])

[<Fact>]
let ``A number with a space before a name stays apart from it`` () =
    unchanged (lines [ "def a = f(100, ms)"; "def b = 1_000 + ms" ])

[<Fact>]
let ``Dimension and unit declarations are written with single spaces`` () =
    let expected =
        """dimension Time

@baseUnit unit ns: Time
unit us: Time = 1_000ns
export unit mm: Length = 0.001m

export dimension Bytes(int)"""
    let written =
        """dimension   Time

@baseUnit   unit ns:Time
unit  us : Time  =  1_000ns
export  unit mm: Length = 0.001m

export dimension Bytes( int )"""
    formatsTo expected written

[<Fact>]
let ``@baseUnit stays on the line of the unit it marks`` () =
    formatsTo "@baseUnit unit ns: Time" "@baseUnit\nunit ns: Time"

[<Fact>]
let ``A module block's declarations stay in the block, @baseUnit on its unit's line`` () =
    unchanged (lines [ "module m ="; "  export dimension Time"; ""; "  @baseUnit unit ns: Time"; "  unit us: Time = 1_000ns" ])

[<Fact>]
let ``A declaration keeps its doc comment and the comment after it`` () =
    unchanged (lines [ "--| How long something takes."; "dimension Time -- a duration"; ""; "--| A second."; "unit s: Time = 1_000ms" ])

[<Fact>]
let ``dimension and unit used as names are formatted as names`` () =
    unchanged (lines [ "def unit = 1"; "def dimension = 2"; "def f = { unit, dimension -> unit + dimension }" ])

[<Fact>]
let ``A derived dimension is written with spaces around * and /, and brackets only on the right`` () =
    unchanged (
        lines
            [ "dimension Speed = Length / Time"
              ""
              "export dimension Acceleration = Length / (Time * Time)"
              ""
              "def f: Length(a) / Time(a), Length / Time / Time -> Length * Length = { a, b ->"
              "  a * a"
              "}" ])

[<Fact>]
let ``A unit of a derived dimension is written with spaces around * and /`` () =
    formatsTo
        (String.concat "\n" [ "unit kmh: Speed = 1km / 1hr"; ""; "unit accel: Acceleration = 1m / 1s / 1s"; ""; "unit area: Area = 1m * 1m" ])
        (String.concat "\n" [ "unit kmh: Speed = 1km/1hr"; ""; "unit accel: Acceleration = 1m /1s/ 1s"; ""; "unit area: Area = 1m*1m" ])
