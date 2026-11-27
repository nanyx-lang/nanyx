/// A dimension's default storage unit, worked out from the units declared with it or set by `@storageUnit`, and its
/// default display unit, which `@baseUnit` marks (#2971).
module Nanyx.Compiler.Tests.Features.StorageAndDisplayUnits

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The units compiling `source` declares, which has no errors.
let private unitsOf (source: string) =
    let result = Compiler.compile (withFinalNewline source)
    Assert.Empty(result.Diagnostics)
    result.Typed.Value.Units

/// The name of the default storage unit and of the default display unit of the dimension `dimension` among `units`.
let private storageAndDisplay (dimension: string) (units: Map<string, UnitInfo>) =
    let of' (pick: UnitInfo -> bool) =
        units.Values |> Seq.filter (fun info -> Dimension.text info.Dimension = dimension && pick info) |> Seq.map _.Name |> List.ofSeq
    of' UnitInfo.isStorage, of' _.IsDisplay

let private distance (marked: string) =
    $"""dimension Distance
{(if marked = "mm" then "@baseUnit " else "")}unit mm: Distance
{(if marked = "m" then "@baseUnit " else "")}unit m: Distance = 1_000mm
{(if marked = "km" then "@baseUnit " else "")}unit km: Distance = 1_000m
"""

[<Fact>]
let ``A dimension is stored in the largest unit every unit is a whole number of, whichever @baseUnit marks`` () =
    for marked in [ "mm"; "m"; "km"; "" ] do
        let units = unitsOf (distance marked)
        let display = if marked = "" then "mm" else marked
        Assert.Equal<string list * string list>(([ "mm" ], [ display ]), storageAndDisplay "Distance" units)
        Assert.Equal("1000000", Rational.text units["km"].Scale)

[<Fact>]
let ``The default storage unit need not be the unit the others are defined from`` () =
    let units = unitsOf "dimension Clock\nunit s: Clock\nunit ms: Clock = 0.001s\nunit min: Clock = 60s\n"
    Assert.Equal<string list * string list>(([ "ms" ], [ "ms" ]), storageAndDisplay "Clock" units)
    Assert.Equal("1000", Rational.text units["s"].Scale)

[<Fact>]
let ``A dimension no unit of which every unit is a whole number of is stored in a unit made up for it`` () =
    let units =
        unitsOf "dimension Distance\n@baseUnit unit m: Distance = 1_000mm\nunit inch: Distance = 25.4mm\nunit mm: Distance\n"
    Assert.Equal<string list * string list>(([], [ "m" ]), storageAndDisplay "Distance" units)
    Assert.Equal("5", Rational.text units["mm"].Scale)
    Assert.Equal("127", Rational.text units["inch"].Scale)
    Assert.Equal("5000", Rational.text units["m"].Scale)
    let unmarked = unitsOf "dimension Distance\nunit mm: Distance\nunit inch: Distance = 25.4mm\n"
    Assert.Equal<string list * string list>(([], [ "mm" ]), storageAndDisplay "Distance" unmarked)

[<Fact>]
let ``A number of a made-up storage unit that isn't whole is named by the decimal the unit is`` () =
    let code, message, _ =
        onlyDiagnostic (withFinalNewline "dimension Distance\nunit mm: Distance\nunit inch: Distance = 25.4mm\ndef x: Distance(int) = 0.1mm")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("'0.1mm' is 1/2 of a '0.2mm'", message)

[<Fact>]
let ``@storageUnit sets the default storage unit, and a unit that is no whole number of it is NYX0300`` () =
    let units = unitsOf "dimension Clock\n@storageUnit unit ns: Clock\nunit us: Clock = 1_000ns\n@baseUnit unit s: Clock = 1_000_000us\n"
    Assert.Equal<string list * string list>(([ "ns" ], [ "s" ]), storageAndDisplay "Clock" units)
    let code, message, at =
        onlyDiagnostic (withFinalNewline "dimension Time\n@storageUnit unit ns: Time\nunit ps: Time = 0.001ns\nunit us: Time = 1_000ns")
    Assert.Equal(DiagnosticCodes.UnitNotWholeStorageUnits.Code, code)
    Assert.Equal(
        "'ps' is 1/1000 of 'ns', the default storage unit of 'Time' that `@storageUnit` marks, but every unit of 'Time' declared with it is a whole number of its default storage unit: mark a unit that 'ps' is a whole number of `@storageUnit`, or mark none to have one worked out",
        message)
    Assert.Equal("unit ps: Time = 0.001ns", at)

[<Fact>]
let ``One unit can be both the default storage and display unit`` () =
    let units = unitsOf "dimension Clock\n@storageUnit @baseUnit unit ns: Clock\nunit us: Clock = 1_000ns\n"
    Assert.Equal<string list * string list>(([ "ns" ], [ "ns" ]), storageAndDisplay "Clock" units)
    Assert.Equal<UnitMark list>([ StorageUnitMark; DisplayUnitMark ], units["ns"].Marks)

/// A module that exports `Time`, stored in nanoseconds.
let private timeModule = "module mod\nexport dimension Time\nexport unit ns: Time\n@baseUnit export unit s: Time = 1_000_000_000ns\n"

[<Fact>]
let ``A unit declared outside its dimension's module is a whole number of its default storage unit, and doesn't change it`` () =
    let whole = "import mod\nunit tick: Time = 10ns\ndef x = 3tick\n"
    let result = compileImporting timeModule whole
    Assert.Empty(result.Diagnostics)
    Assert.Equal("10", Rational.text result.Typed.Value.Units["tick"].Scale)
    Assert.Equal("1", Rational.text result.Typed.Value.Units["ns"].Scale)
    let fraction = "import mod\nunit ps: Time = 0.001ns\n"
    let code, message, at = onlyDiagnosticIn fraction (compileImporting timeModule fraction)
    Assert.Equal(DiagnosticCodes.UnitNotWholeStorageUnits.Code, code)
    Assert.Equal(
        "'ps' is 1/1000 of 'ns', the default storage unit of 'Time', which is declared in another module: a unit of it declared here is a whole number of its default storage unit, so that it doesn't change what a 'Time' holds",
        message)
    Assert.Equal("unit ps: Time = 0.001ns", at)

[<Fact>]
let ``A unit declared outside its dimension's module that is no whole number of a made-up storage unit names it as a decimal`` () =
    let distanceModule = "module mod\nexport dimension Distance\nexport unit mm: Distance\nexport unit inch: Distance = 25.4mm\n"
    let fraction = "import mod\nunit thou: Distance = 0.001inch\n"
    let code, message, at = onlyDiagnosticIn fraction (compileImporting distanceModule fraction)
    Assert.Equal(DiagnosticCodes.UnitNotWholeStorageUnits.Code, code)
    Assert.Equal(
        "'thou' is 127/1000 of 0.2mm, the default storage unit of 'Distance', which is declared in another module: a unit of it declared here is a whole number of its default storage unit, so that it doesn't change what a 'Distance' holds",
        message)
    Assert.Equal("unit thou: Distance = 0.001inch", at)
