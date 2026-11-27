/// Numbers with units (#2798): `500ms` is a measured value of `ms`'s dimension, stored as the number the code around it
/// expects, or the one its dimension is constrained to, or else the one it is written as, and is its number of base
/// units there.
module Nanyx.Compiler.Tests.Features.UnitLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private dimensions =
    "dimension Time\n@baseUnit unit ns: Time\nunit us: Time = 1_000ns\nunit ms: Time = 1_000us\nunit s: Time = 1_000ms\n"
    + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\n"
    + "dimension Bytes(int)\n@baseUnit unit B: Bytes\nunit KB: Bytes = 1_000B\n"

/// The one diagnostic of compiling `source`, after the dimensions: its code and message.
let private only (source: string) =
    let code, message, _ = onlyDiagnostic (withFinalNewline (dimensions + source))
    code, message

/// The types of `names`, as compiling `source` after the dimensions infers them, which reports nothing.
let private typesIn (source: string) (names: string list) =
    let result = Compiler.compile (withFinalNewline (dimensions + source))
    Assert.Empty(result.Diagnostics)
    names |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types[name])

[<Fact>]
let ``A number with a unit is stored as the number it is written as, where nothing expects another`` () =
    Assert.Equal<string list>([ "Time(float)"; "Time(int)" ], typesIn "def t = 0.5s\ndef u = 500ms" [ "t"; "u" ])

[<Fact>]
let ``A number with a unit is stored as the number expected`` () =
    Assert.Equal<string list>(
        [ "Time(float)"; "Time(int)"; "Time(I32)" ],
        typesIn "def v: Time(float) = 500ms\ndef w: Time(int) = 0.5s\ndef x: Time(I32) = 1s" [ "v"; "w"; "x" ])

[<Fact>]
let ``A number with a unit of a constrained dimension is stored as its dimension's storage`` () =
    Assert.Equal<string list>([ "Length(float)"; "Bytes(int)" ], typesIn "def y = 5mm\ndef z = 50.5KB" [ "y"; "z" ])

[<Fact>]
let ``A function takes a number with a unit of its parameter's dimension`` () =
    Assert.Empty(reportedAt (dimensions + "def isLong: Time -> bool = { t -> true }\ndef long = isLong(2s)\ndef ints: Time(int) -> bool = { t -> true }\ndef also = ints(0.5s)"))

[<Fact>]
let ``Numbers with units of two storages are of two types`` () =
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (reportedAt (dimensions + "def both = [0.5s, 500ms]")).Head |> fst)

[<Fact>]
let ``In integer storage, a number with a unit that is no whole number of default storage units is an error with the fix`` () =
    let code, message = only "def x: Time(int) = 1.5ns"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("'1.5ns' is 3/2 of a 'ns', which is no whole number of 'ns'", message)

[<Fact>]
let ``A number with a unit that doesn't fit its storage is NYX0229`` () =
    let code, message = only "def x: Time(I8) = 1s"
    Assert.Equal(DiagnosticCodes.LiteralOutOfRange.Code, code)
    Assert.Contains("'1s' is 1000000000 'ns'", message)

[<Fact>]
let ``A number with no unit is never a measured value`` () =
    // Only the mismatch: the storage of `isLong`'s generic `Time` is the literal's kind where the call looks for `Num`
    // of it, so the context is found, not reportedAt (dimensions + missing) too (#2804).
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.TypeMismatch.Code, "0" ],
        reportedAt (dimensions + "def isLong: Time -> bool = { t -> true }\ndef x = isLong(0)"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.TypeMismatch.Code, "0.5" ],
        reportedAt (dimensions + "def isLong: Time -> bool = { t -> true }\ndef run = { -> isLong(0.5) }"))
    let _, message, _ =
        (Compiler.compile (withFinalNewline (dimensions + "def x: Time(int) = 0"))).Diagnostics
        |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, ())
        |> List.head
    Assert.Contains("'0' has no unit, so it is no 'Time'", message)

[<Fact>]
let ``A guard compares with a number with a unit`` () =
    Assert.Empty(reportedAt (dimensions + "def f: Time(int) -> bool = { t ->\n  match t\n    | == 0ms -> true\n    | _ -> false\n}"))

[<Fact>]
let ``dimension and unit are names a program can still use`` () =
    Assert.Empty((Compiler.compile "def unit = 1\ndef dimension = 2\ndef sum = unit + dimension\n").Diagnostics)

[<Fact>]
let ``A division of two storages that don't agree is the error where it is passed to a call, not the call's context`` () =
    // The `/` was left unsettled, so `println` reportedAt (dimensions + that) `ToString` of it is missing, and not the mismatch (#2852).
    Assert.Equal<(string * string) list>([ DiagnosticCodes.TypeMismatch.Code, "1.5s / 1ms" ], reportedAt (dimensions + "println(1.5s / 1ms)"))
    Assert.Equal<(string * string) list>([ DiagnosticCodes.TypeMismatch.Code, "1.5 / 1" ], reportedAt (dimensions + "println(1.5 / 1)"))
