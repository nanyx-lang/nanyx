/// The checks of dimension and unit declarations (#2622): a dimension is a type, each has one unit that is no multiple
/// of another, every other unit is an exact multiple of one of the same dimension, and units are exported and imported
/// as types are.
module Nanyx.Compiler.Tests.Features.UnitDeclarations

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

/// The scale of each unit compiling `source` declares, to its dimension's default storage unit, as text.
let private scales (source: string) =
    let result = Compiler.compile (withFinalNewline source)
    Assert.Empty(result.Diagnostics)
    result.Typed.Value.Units |> Map.map (fun _ info -> Rational.text info.Scale)

[<Fact>]
let ``Dimensions and units that are declared rightly have no errors`` () =
    let source = time + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\ndimension Bytes(int)\n@baseUnit unit B: Bytes\nunit KiB: Bytes = 1_024B"
    Assert.Empty(reportedAt source)

[<Fact>]
let ``Each unit's scale to its dimension's default storage unit is exact`` () =
    let found = scales (time + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\nunit km: Length = 1_000m\nunit um: Length = 0.001mm\nunit half: Length = 0.5km")
    Assert.Equal("1", found["ns"])
    Assert.Equal("1000000000", found["s"])
    Assert.Equal("1", found["um"])
    Assert.Equal("1000", found["mm"])
    Assert.Equal("1000000", found["m"])
    Assert.Equal("500000000", found["half"])

[<Fact>]
let ``A dimension is a type, generic in its storage unless it is constrained`` () =
    let source = time + "dimension Bytes(int)\n@baseUnit unit B: Bytes\ndef f: Time(int) -> Time(int) = { t -> t }\ndef g: Bytes -> Bytes = { b -> b }"
    Assert.Empty(reportedAt source)
    let typed = (Compiler.compile (withFinalNewline source)).Typed.Value
    Assert.True(typed.TypeDefs["Time"].IsDimension)
    Assert.Equal(1, typed.TypeDefs["Time"].Parameters.Length)
    Assert.Empty(typed.TypeDefs["Bytes"].Parameters)

[<Fact>]
let ``A dimension with no units is NYX0286 at the dimension`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitsNotFromOne.Code, "dimension Time" ],
        reportedAt "dimension Time\n")

[<Fact>]
let ``A second unit that is no multiple of another is NYX0286 at the unit`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitsNotFromOne.Code, "unit q: Time" ],
        reportedAt (time + "unit q: Time"))
    let _, message, _ = onlyDiagnostic (withFinalNewline (time + "unit q: Time"))
    Assert.Equal(
        "'q' is no multiple of another unit, but 'ns' is the one the units of 'Time' are defined from: define 'q' as a multiple of a unit of 'Time', as `unit ms: Time = 1_000us` is",
        message)

[<Fact>]
let ``A second @baseUnit or @storageUnit of a dimension is NYX0287 at the unit`` () =
    let code = DiagnosticCodes.MisplacedUnitAttribute.Code
    Assert.Equal<(string * string) list>([ code, "unit ms: Time = 1_000us" ], reportedAt (time.Replace("unit ms", "@baseUnit unit ms")))
    Assert.Equal<(string * string) list>(
        [ code, "unit us: Time = 1_000ns" ],
        reportedAt (time.Replace("@baseUnit unit ns", "@storageUnit unit ns").Replace("unit us", "@storageUnit unit us")))

[<Fact>]
let ``Units defined in a circle are NYX0288 at each of them`` () =
    let code = DiagnosticCodes.UnitCycle.Code
    Assert.Equal<(string * string) list>(
        [ code, "unit a: Time = 2b"; code, "unit b: Time = 3a" ],
        reportedAt (time + "unit a: Time = 2b\nunit b: Time = 3a"))
    Assert.Equal<(string * string) list>([ code, "unit a: Time = 2a" ], reportedAt (time + "unit a: Time = 2a"))

[<Fact>]
let ``A unit defined in another dimension's units is NYX0289 at its multiple`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitOfAnotherDimension.Code, "3ms" ],
        reportedAt (time + "dimension Length(float)\n@baseUnit unit m: Length\nunit bad: Length = 3ms"))

[<Fact>]
let ``A dimension stored in a type with no Num is NYX0290 at the storage`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DimensionStorageNotNumber.Code, "bool" ],
        reportedAt "dimension Flags(bool)\n@baseUnit unit f: Flags")
    Assert.Empty(reportedAt "dimension Small(I32)\n@baseUnit unit u: Small")

[<Fact>]
let ``An unknown unit is NYX0291, in a literal or in a unit's definition`` () =
    let code = DiagnosticCodes.UnknownUnit.Code
    Assert.Equal<(string * string) list>([ code, "3qq" ], reportedAt (time + "def x = 3qq"))
    Assert.Equal<(string * string) list>([ code, "3zz" ], reportedAt (time + "unit c: Time = 3zz"))
    let _, message, _ = onlyDiagnostic (withFinalNewline (time + "def x = 3mss"))
    Assert.Contains("did you mean 'ms'?", message)

[<Fact>]
let ``A unit of a dimension that isn't one is NYX0109`` () =
    let code = DiagnosticCodes.UnknownType.Code
    Assert.Equal<(string * string) list>([ code, "unit d: Tme = 2ns" ], reportedAt (time + "unit d: Tme = 2ns"))
    Assert.Equal<(string * string) list>([ code, "unit d: Point = 2ns" ], reportedAt (time + "type Point = (x: int)\nunit d: Point = 2ns"))

[<Fact>]
let ``A unit declared twice is NYX0113 at the second`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DuplicateDefinition.Code, "unit ms: Time = 1_000us" ],
        reportedAt (time + "unit ms: Time = 1_000us"))

[<Fact>]
let ``A unit's name is apart from values: a value of the same name changes nothing`` () =
    Assert.Empty(reportedAt (time + "def ms = 3\ndef x = 500ms\ndef y = ms + 1"))

/// A module that exports `Time` and its units, but for `secret`.
let private timeModule =
    "module mod\nexport dimension Time\n@baseUnit export unit ns: Time\nexport unit us: Time = 1_000ns\nexport unit ms: Time = 1_000us\nexport unit tick: Time = 10ns\nunit secret: Time = 2ms\n"

[<Fact>]
let ``An import brings in the units its module exports, aliased or not`` () =
    for import in [ "import mod"; "import mod as t" ] do
        let main = $"{import}\ndef x = 500ms\ndef y = 2secret\n"
        Assert.Equal<(string * string) list>(
            [ DiagnosticCodes.UnknownUnit.Code, "2secret" ],
            reportedAtIn main (compileImporting timeModule main))

[<Fact>]
let ``An import that selects names brings in only the units it selects`` () =
    let main = "import mod as (Time, ms)\ndef x = 500ms\ndef y = 2tick\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnknownUnit.Code, "2tick" ],
        reportedAtIn main (compileImporting timeModule main))

[<Fact>]
let ``A module adds units to an imported dimension, but doesn't mark them`` () =
    let main = "import mod\nunit tock: Time = 20ns\nunit min: Time = 60_000ms\n"
    let result = compileImporting timeModule main
    Assert.Empty(result.Diagnostics)
    Assert.Equal("20", Rational.text result.Typed.Value.Units["tock"].Scale)
    Assert.Equal("60000000000", Rational.text result.Typed.Value.Units["min"].Scale)
    for attribute in [ "@baseUnit"; "@storageUnit" ] do
        let marked = $"import mod\n{attribute} unit tock: Time = 20ns\n"
        Assert.Equal<(string * string) list>(
            [ DiagnosticCodes.MisplacedUnitAttribute.Code, "unit tock: Time = 20ns" ], reportedAtIn marked (compileImporting timeModule marked))
    let first = "import mod\nunit ps: Time\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitsNotFromOne.Code, "unit ps: Time" ], reportedAtIn first (compileImporting timeModule first))

/// A module `name` that exports a dimension `dimension` with the default storage unit `ms`.
let private withMs (name: string) (dimension: string) =
    $"module {name}\nexport dimension {dimension}\n@baseUnit export unit ms: {dimension}\nexport unit other{name}: {dimension} = 2ms\n"

[<Fact>]
let ``Two imports that bring in units of one name are NYX0105 where the name is written`` () =
    let modules = [ "a.nyx", withMs "a" "Time"; "b.nyx", withMs "b" "Millis" ]
    let main = "import a\nimport b\ndef x = 500ms\ndef y = 2othera\nunit z: Time = 3ms\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.AmbiguousName.Code, "500ms"; DiagnosticCodes.AmbiguousName.Code, "3ms" ],
        reportedAtIn main (compileImportingAll modules main) |> List.sortBy (fun (_, text) -> main.IndexOf text))
    let selecting = "import a\nimport b as (Millis)\ndef x = 500ms\n"
    Assert.Empty(reportedAtIn selecting (compileImportingAll modules selecting))
    let own = "import a\nimport b\nunit ms: Time = 1othera\ndef x = 500ms\n"
    Assert.Empty(reportedAtIn own (compileImportingAll modules own))

[<Fact>]
let ``A unit of a float dimension past a float's largest is NYX0313 at the unit, and no value is Infinity (#3114)`` () =
    // `tiny` is the default storage unit, so `m` is 1e320 of it.
    let source = "dimension Length(float)\n@baseUnit unit m: Length\nunit tiny: Length = 1.0e-320m\ndef a = 1.0m"
    let code, message, text = onlyDiagnostic (withFinalNewline source)
    Assert.Equal(DiagnosticCodes.UnitPastStorage.Code, code)
    Assert.Equal("unit m: Length", text)
    Assert.Equal(
        "'m' is about 1.0e320 of 'tiny', the default storage unit of 'Length', more than its storage, 'float', holds, so no value of it could be stored: define the units of 'Length' closer in size",
        message)

[<Fact>]
let ``Each unit of an int dimension past an int's largest is NYX0313 at the unit, and no literal is out of range for it (#3114)`` () =
    let units = [ 1..64 ] |> List.map (fun n -> $"unit u{n}: Big = 2u{n - 1}") |> String.concat "\n"
    let source = $"dimension Big(int)\n@baseUnit unit u0: Big\n{units}\ndef a = 1u0"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitPastStorage.Code, "unit u63: Big = 2u62"; DiagnosticCodes.UnitPastStorage.Code, "unit u64: Big = 2u63" ],
        reportedAt source)
    let messages = (Compiler.compile (withFinalNewline source)).Diagnostics |> List.map _.Message
    Assert.Equal(
        "'u63' is 9223372036854775808 of 'u0', the default storage unit of 'Big', more than its storage, 'int', holds, so no value of it could be stored: define the units of 'Big' closer in size, or store it as a `float`",
        messages.Head)
