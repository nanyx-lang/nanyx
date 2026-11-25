/// The checks of dimension and unit declarations (#2622): a dimension is a type, each has one base unit, every other
/// unit is an exact multiple of one of the same dimension, and units are exported and imported as types are.
module Nanyx.Compiler.Tests.Features.UnitDeclarations

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The code and the text each diagnostic of `result`, compiled from `source`, is reported at.
let private reportedIn (source: string) (result: CompileResult) =
    let source = normalized source
    result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

/// The code and the text each diagnostic of compiling `source` is reported at.
let private reported (source: string) =
    let source = withFinalNewline source
    reportedIn source (Compiler.compile source)

let private time =
    "dimension Time\n@baseUnit unit ns: Time\nunit us: Time = 1_000ns\nunit ms: Time = 1_000us\nunit s: Time = 1_000ms\n"

/// The scale of each unit compiling `source` declares, to its dimension's base unit, as text.
let private scales (source: string) =
    let result = Compiler.compile (withFinalNewline source)
    Assert.Empty(result.Diagnostics)
    result.Typed.Value.Units |> Map.map (fun _ info -> Rational.text info.Scale)

[<Fact>]
let ``Dimensions and units that are declared rightly have no errors`` () =
    let source = time + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\ndimension Bytes(int)\n@baseUnit unit B: Bytes\nunit KiB: Bytes = 1_024B"
    Assert.Empty(reported source)

[<Fact>]
let ``Each unit's scale to its dimension's base unit is exact`` () =
    let found = scales (time + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\nunit km: Length = 1_000m\nunit um: Length = 0.001mm\nunit half: Length = 0.5km")
    Assert.Equal("1", found["ns"])
    Assert.Equal("1000000000", found["s"])
    Assert.Equal("1/1000", found["mm"])
    Assert.Equal("1/1000000", found["um"])
    Assert.Equal("500", found["half"])

[<Fact>]
let ``A dimension is a type, generic in its storage unless it is constrained`` () =
    let source = time + "dimension Bytes(int)\n@baseUnit unit B: Bytes\ndef f: Time(int) -> Time(int) = { t -> t }\ndef g: Bytes -> Bytes = { b -> b }"
    Assert.Empty(reported source)
    let typed = (Compiler.compile (withFinalNewline source)).Typed.Value
    Assert.True(typed.TypeDefs["Time"].IsDimension)
    Assert.Equal(1, typed.TypeDefs["Time"].Parameters.Length)
    Assert.Empty(typed.TypeDefs["Bytes"].Parameters)

[<Fact>]
let ``A dimension with no base unit is NYX0286 at the dimension`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DimensionWithoutBaseUnit.Code, "dimension Time" ],
        reported "dimension Time\n")

[<Fact>]
let ``A unit that is neither a base unit nor a multiple is NYX0286 at the unit`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DimensionWithoutBaseUnit.Code, "unit q: Time" ],
        reported (time + "unit q: Time"))

[<Fact>]
let ``A second base unit, and a base unit that is a multiple, are NYX0287`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MisplacedBaseUnit.Code, "unit ps: Time" ], reported (time + "@baseUnit unit ps: Time"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MisplacedBaseUnit.Code, "1_000ps" ],
        reported "dimension Time\n@baseUnit unit ns: Time = 1_000ps\nunit ps: Time = 0.001ns")

[<Fact>]
let ``Units defined in a circle are NYX0288 at each of them`` () =
    let code = DiagnosticCodes.UnitCycle.Code
    Assert.Equal<(string * string) list>(
        [ code, "unit a: Time = 2b"; code, "unit b: Time = 3a" ],
        reported (time + "unit a: Time = 2b\nunit b: Time = 3a"))
    Assert.Equal<(string * string) list>([ code, "unit a: Time = 2a" ], reported (time + "unit a: Time = 2a"))

[<Fact>]
let ``A unit defined in another dimension's units is NYX0289 at its multiple`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnitOfAnotherDimension.Code, "3ms" ],
        reported (time + "dimension Length(float)\n@baseUnit unit m: Length\nunit bad: Length = 3ms"))

[<Fact>]
let ``A dimension stored in a type with no Num is NYX0290 at the storage`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DimensionStorageNotNumber.Code, "bool" ],
        reported "dimension Flags(bool)\n@baseUnit unit f: Flags")
    Assert.Empty(reported "dimension Small(I32)\n@baseUnit unit u: Small")

[<Fact>]
let ``An unknown unit is NYX0291, in a literal or in a unit's definition`` () =
    let code = DiagnosticCodes.UnknownUnit.Code
    Assert.Equal<(string * string) list>([ code, "3qq" ], reported (time + "def x = 3qq"))
    Assert.Equal<(string * string) list>([ code, "3zz" ], reported (time + "unit c: Time = 3zz"))
    let _, message, _ = onlyDiagnostic (withFinalNewline (time + "def x = 3mss"))
    Assert.Contains("did you mean 'ms'?", message)

[<Fact>]
let ``A unit of a dimension that isn't one is NYX0109`` () =
    let code = DiagnosticCodes.UnknownType.Code
    Assert.Equal<(string * string) list>([ code, "unit d: Tme = 2ns" ], reported (time + "unit d: Tme = 2ns"))
    Assert.Equal<(string * string) list>([ code, "unit d: Point = 2ns" ], reported (time + "type Point = (x: int)\nunit d: Point = 2ns"))

[<Fact>]
let ``A unit declared twice is NYX0113 at the second`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.DuplicateDefinition.Code, "unit ms: Time = 1_000us" ],
        reported (time + "unit ms: Time = 1_000us"))

[<Fact>]
let ``A unit's name is apart from values: a value of the same name changes nothing`` () =
    Assert.Empty(reported (time + "def ms = 3\ndef x = 500ms\ndef y = ms + 1"))

/// A module that exports `Time` and its units, but for `secret`.
let private timeModule =
    "module mod\nexport dimension Time\n@baseUnit export unit ns: Time\nexport unit us: Time = 1_000ns\nexport unit ms: Time = 1_000us\nunit secret: Time = 2ms\n"

[<Fact>]
let ``An import brings in the units its module exports, aliased or not`` () =
    for import in [ "import mod"; "import mod as t" ] do
        let main = $"{import}\ndef x = 500ms\ndef y = 2secret\n"
        Assert.Equal<(string * string) list>(
            [ DiagnosticCodes.UnknownUnit.Code, "2secret" ],
            reportedIn main (compileImporting timeModule main))

[<Fact>]
let ``An import that selects names brings in only the units it selects`` () =
    let main = "import mod as (Time, ms)\ndef x = 500ms\ndef y = 2us\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnknownUnit.Code, "2us" ],
        reportedIn main (compileImporting timeModule main))

[<Fact>]
let ``A module adds units to an imported dimension, but not its base unit`` () =
    let main = "import mod\nunit ps: Time = 0.001ns\nunit min: Time = 60_000ms\n"
    let result = compileImporting timeModule main
    Assert.Empty(result.Diagnostics)
    Assert.Equal("1/1000", Rational.text result.Typed.Value.Units["ps"].Scale)
    Assert.Equal("60000000000", Rational.text result.Typed.Value.Units["min"].Scale)
    let base' = "import mod\n@baseUnit unit ps: Time\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MisplacedBaseUnit.Code, "unit ps: Time" ], reportedIn base' (compileImporting timeModule base'))

/// A module `name` that exports a dimension `dimension` with the base unit `ms`.
let private withMs (name: string) (dimension: string) =
    $"module {name}\nexport dimension {dimension}\n@baseUnit export unit ms: {dimension}\nexport unit other{name}: {dimension} = 2ms\n"

[<Fact>]
let ``Two imports that bring in units of one name are NYX0105 where the name is written`` () =
    let modules = [ "a.nyx", withMs "a" "Time"; "b.nyx", withMs "b" "Millis" ]
    let main = "import a\nimport b\ndef x = 500ms\ndef y = 2othera\nunit z: Time = 3ms\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.AmbiguousName.Code, "500ms"; DiagnosticCodes.AmbiguousName.Code, "3ms" ],
        reportedIn main (compileImportingAll modules main) |> List.sortBy (fun (_, text) -> main.IndexOf text))
    let selecting = "import a\nimport b as (Millis)\ndef x = 500ms\n"
    Assert.Empty(reportedIn selecting (compileImportingAll modules selecting))
    let own = "import a\nimport b\nunit ms: Time = 1othera\ndef x = 500ms\n"
    Assert.Empty(reportedIn own (compileImportingAll modules own))
