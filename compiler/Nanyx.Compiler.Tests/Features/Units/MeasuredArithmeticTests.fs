/// Arithmetic on measured values (#2799): each row of the Arithmetic table on the Units and dimensions page, at a
/// concrete storage and a generic one, `Add` and `Sub` of a dimension, and the errors.
module Nanyx.Compiler.Tests.Features.MeasuredArithmetic

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

/// The code and message of the one diagnostic compiling `source` after the dimensions gives.
let private only (source: string) =
    let code, message, _ = onlyDiagnostic (withFinalNewline (dimensions + source))
    code, message

[<Fact>]
let ``Sums, differences, scalings and remainders keep the dimension`` () =
    Assert.Equal<string list>(
        [ "Time(int)"; "Time(int)"; "Time(int)"; "Time(int)"; "Time(int)"; "Time(int)" ],
        typesAfter dimensions "def a = 1s + 2ms\ndef b = 1s - 2ms\ndef c = 2 * 1s\ndef d = 1s * 2\ndef e = -1500ms % 1s\ndef f = -(1s)" [ "a"; "b"; "c"; "d"; "e"; "f" ])

[<Fact>]
let ``A measured value divided by a number is of the storage's fraction, and floored of the storage`` () =
    Assert.Equal<string list>([ "Time(float)"; "Time(int)" ], typesAfter dimensions "def a = 1s / 2\ndef b = 1s // 3" [ "a"; "b" ])

[<Fact>]
let ``A measured value divided by one of its dimension is a plain number`` () =
    Assert.Equal<string list>([ "float"; "int" ], typesAfter dimensions "def a = 1s / 1ms\ndef b = 1500ms // 1s" [ "a"; "b" ])

[<Fact>]
let ``Each row holds at a generic storage`` () =
    let source =
        "def add: Time(a), Time(a) -> Time(a) = { x, y -> x + y }\n"
        + "def half: Time(a) -> Time(fraction) = { t -> t / 2 }\n"
        + "def halfDown: Time(a) -> Time(a) = { t -> t // 2 }\n"
        + "def ratio: Time(a), Time(a) -> fraction = { x, y -> x / y }\n"
        + "def twice: Time(a) -> Time(a) = { t -> t * 2 }\n"
        + "def later: Time(a) -> Time(a) = { t -> t + 5ms }\n"
        + "def left: Time(a), Time(a) -> Time(a) = { x, y -> x % y }"
    Assert.Equal<string list>(
        [ "Time(a), Time(a) -> Time(a)"; "Time(a) -> Time(fraction)"; "Time(a) -> Time(a)"; "Time(a), Time(a) -> fraction"
          "Time(a) -> Time(a)"; "Time(a) -> Time(a)"; "Time(a), Time(a) -> Time(a)" ],
        typesAfter dimensions source [ "add"; "half"; "halfDown"; "ratio"; "twice"; "later"; "left" ])

[<Fact>]
let ``A dimension has Add and Sub of its storage, so a function generic in Add sums times`` () =
    Assert.Equal<string list>(
        [ "Time(int)"; "Time(float)"; "Time(int)" ],
        typesAfter dimensions "def sum: [Add(a)] a, a -> a = { x, y -> x + y }\ndef a = sum(100ms, 200ms)\ndef b = sum(0.5s, 0.25s)\ndef none: [Add(a)] a -> a = { x -> zero }\ndef c = none(5ms)" [ "a"; "b"; "c" ])

[<Fact>]
let ``Measured values compare as their storages do`` () =
    Assert.Equal<string list>([ "bool"; "bool" ], typesAfter dimensions "def a = 1s == 1_000ms\ndef b = 1s < 2s" [ "a"; "b" ])

[<Fact>]
let ``Adding two dimensions is an error naming both`` () =
    let code, message = only "def x = 1ms + 2m"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("'Time' and 'Length' are two", message)

[<Fact>]
let ``A number with no unit is no operand of a sum of measured values`` () =
    let code, message = only "def x = 5 + 1ms"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("a number with no unit is of none", message)

[<Fact>]
let ``A power of a measured value is an error that says to multiply it by itself`` () =
    let code, message = only "def x = 1ms ** 2"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("multiply it by itself", message)

[<Fact>]
let ``Beside a generic storage, a number with a unit that is no whole number of default storage units is an error`` () =
    let code, message = only "def later: Time(a) -> Time(a) = { t -> t + 0.5ns }"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("'0.5ns' is 1/2 of its default storage unit", message)
