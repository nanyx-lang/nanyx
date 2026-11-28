/// A format spec written in a hole, `"{price:.2f}"` (#3173): a call to `__format`, and a spec its value's type rejects
/// is NYX0307 at the spec, as the compiler's `FormatSpecs` reads it, which agrees with the prelude's on one table.
module Nanyx.Compiler.Tests.Features.Strings.FormatSpecs

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

let private timeUnits = [ "d"; "hr"; "min"; "s"; "ms"; "us"; "ns" ]

let private formattedOf (typeName: string) =
    match typeName with
    | "int" | "I8" | "I16" | "I32" | "U8" | "U16" | "U32" | "U64" -> FormatSpecs.Formatted.Integer
    | "float" | "F32" -> FormatSpecs.Formatted.Float
    | "string" | "char" | "bool" -> FormatSpecs.Formatted.Text
    | "Instant" -> FormatSpecs.Formatted.Instant
    | "Time" -> FormatSpecs.Formatted.Measured("Time", timeUnits, timeUnits |> List.map (fun name -> name, 0), [ 1 ], [ "Time" ])
    | "Time * Time" -> FormatSpecs.Formatted.Measured("Time * Time", [], timeUnits |> List.map (fun name -> name, 0), [ 2 ], [ "Time" ])
    | other -> failwith $"No row's type is {other}"

[<Fact>]
let ``The compiler reads each spec of the shared table as the prelude does`` () =
    let outcome (typeName, _, spec, _) =
        match FormatSpecs.check (formattedOf typeName) spec with
        | None -> typeName, spec, FormatSpecTable.Takes
        | Some bad -> typeName, spec, FormatSpecTable.Rejects(bad.At, bad.Expected)
    let differing =
        FormatSpecTable.rows
        |> List.choose (fun row ->
            let typeName, _, spec, written = row
            let _, _, read = outcome row
            if read = written then None else Some (typeName, spec, read))
    Assert.Equal<(string * string * FormatSpecTable.Outcome) list>([], differing)

[<Fact>]
let ``A colon inside brackets in a hole is not a spec`` () =
    let typed = compileClean (withFinalNewline "def f = { x: int -> x + 1 }\ndef s = \"{f(1)} {[1, 2] \\list.map({ x: int -> x })}\"")
    Assert.NotEmpty typed.Module

[<Fact>]
let ``A spec its value's type rejects is NYX0307 at the character it stops at`` () =
    let source = withFinalNewline "def price = 3.14159\ndef s = \"cost {price:.2q}\""
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.BadFormatSpec.Code, code)
    Assert.Equal("q", at)
    Assert.Equal("'.2q' is no format spec for a 'float': at 2, it could have one of `e`, `E`, `f`, `F`, `g`, `G` or `%`", message)

[<Fact>]
let ``Each type the prelude formats has its spec checked, and a spec that ends too soon is reported on the whole spec`` () =
    let source =
        withFinalNewline "def s = \"{42:,x} {\"ab\":+} {true:=5} {Instant.epoch:%Q} {1.5:.} {I8.fromInt(1):z} {F32.fromInt(1):d}\""
    Assert.Equal<(string * string) list>(
        [ for at in [ ","; "+"; "="; "Q"; "."; "z"; "d" ] -> DiagnosticCodes.BadFormatSpec.Code, at ],
        reportedAt source)

[<Fact>]
let ``A unit quantity's spec is checked with the units its module sees, a derived dimension's own among them`` () =
    let source =
        withFinalNewline """dimension Length(float)
@baseUnit unit m: Length
unit km: Length = 1000m
dimension Speed = Length / Time
unit kmh: Speed = 1km / 1hr
def ok = "{1500ms:.1f s} {2.5km:.0f m} {36.0km / 1hr:.1f kmh} {1500ms:+.2f}"
def s = "{2.5km:.1f parsec}"
"""
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.BadFormatSpec.Code, code)
    Assert.Equal("p", at)
    Assert.Equal("'.1f parsec' is no format spec for a 'Length(float)': at 4, it could have the name of a unit of Length: `km` or `m`", message)

[<Fact>]
let ``A composite unit in a hole's spec is checked, and one whose units make another dimension says what they make`` () =
    let source =
        withFinalNewline """dimension Length(float)
@baseUnit unit m: Length
unit km: Length = 1000m
def ok = "{36.0km / 1hr:.1f km/hr} {2.0m * 3.0m:.0f m*m}"
def s = "{36.0km / 1hr:.1f km*hr}"
"""
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.BadFormatSpec.Code, code)
    Assert.Equal("k", at)
    Assert.Equal("'.1f km*hr' is no format spec for a 'Length(float) / Time(float)': at 4, it could have units that make Length / Time: `km*hr` makes Length * Time", message)

[<Fact>]
let ``A spec for a type variable's value, or a type of the program's own, is left to the program as it runs`` () =
    compileClean (
        withFinalNewline """type Money = (cents: int)
def Money.tryFormat: Money, string -> #some(string) | #badFormat(BadFormat) = { m, spec -> #some("{m.cents}") }
def show: [Format(a)] a -> string = { x -> "{x:nonsense}" }
def s = "{Money(cents = 5):nonsense}" ++ show(1)""")
    |> ignore
