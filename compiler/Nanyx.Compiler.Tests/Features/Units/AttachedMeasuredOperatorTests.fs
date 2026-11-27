/// An operator attached to a type of its own, beside a measured value (#2873): it is the one called, as it is beside any
/// other operand, and the units rule reads the operator only where the type attaches none.
module Nanyx.Compiler.Tests.Features.AttachedMeasuredOperators

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

/// `Stamp`, a private type, with a `+` that moves it by a time and a `-` that gives the time between two.
let private stamp =
    "type Stamp = private int\n"
    + "def Stamp.`+`: Stamp, Time(int) -> Stamp = { Stamp(a), t -> Stamp(a + t // 1ns) }\n"
    + "def Stamp.`-`: Stamp, Stamp -> Time(int) = { Stamp(a), Stamp(b) -> (a - b) * 1ns }\n"
    + "def s = Stamp(5)\n"

[<Fact>]
let ``An operator a type attaches that takes a measured value is the one called`` () =
    Assert.Equal<string list>([ "Stamp"; "Time(int)" ], typesAfter stamp "def later = s + 3s\ndef between = later - s" [ "later"; "between" ])

[<Fact>]
let ``An operator a type attaches that takes and gives a measured value is the one called`` () =
    let source = "type Offset = private int\ndef Offset.`+`: Offset, Time(int) -> Time(int) = { Offset(n), t -> t + n * 1s }\ndef moved = Offset(2) + 3s"
    let result = Compiler.compile (withFinalNewline source)
    Assert.Empty(result.Diagnostics)
    Assert.Equal("Time(int)", Unifier.tyToString result.Typed.Value.Types["moved"])

[<Fact>]
let ``A number beside a measured value is still read by the units rule, though its type attaches the operator`` () =
    Assert.Equal<string list>([ "Time(int)"; "Time(int)" ], typesAfter stamp "def twice = 2 * 3s\ndef sum = 3s + 2s" [ "twice"; "sum" ])

[<Fact>]
let ``Beside a measured value, an attached operator's parameters are checked as a call's are`` () =
    // `Stamp`'s `-` takes two stamps, so a time is the wrong argument to it, not a value of the wrong dimension.
    let code, message, _ = onlyDiagnostic (withFinalNewline (stamp + "def earlier = s - 3s"))
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'Time(int)' vs 'Stamp'", message)

[<Fact>]
let ``Beside a measured value, a type that attaches no such operator still says so`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline "type Plain = private int
def p = Plain(1) + 3s")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'Plain' has no `+`: attach one, as in ``def Plain.`+` = ...``, or load an instance, `use Add(Plain)(...)`", message)
