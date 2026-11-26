module Nanyx.Compiler.Tests.Features.OwnTypeOperators

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// An arithmetic operator on a type of its own, a private type such as `Money`, calls the
// definition the type attaches under the operator's name. One that nothing gives the operator says so, rather than
// that the type is not an `int`, which the built-in operator would have needed (#2843).

let private money = "type Money = private int\n\n"

[<Fact>]
let ``An operator on a private type that has none says so, and how to give it one`` () =
    let cases =
        [ "a - b", "'Money' has no `-`: attach one, as in ``def Money.`-` = ...``, or load an instance, `use Sub(Money)(...)`"
          "a + b", "'Money' has no `+`: attach one, as in ``def Money.`+` = ...``, or load an instance, `use Add(Money)(...)`"
          "a * b", "'Money' has no `*`: attach one, as in ``def Money.`*` = ...``, or load an instance, `use Num(Money)(...)`"
          "-a", "'Money' has no prefix `-`: attach `negate`, as in `def Money.negate = ...`, or load an instance, `use Sub(Money)(...)`" ]
    for operation, message in cases do
        let source = money + $"def f = {{ a: Money, b: Money -> {operation} }}\n"
        Assert.Equal((DiagnosticCodes.TypeMismatch.Code, message, operation), onlyDiagnostic source)

/// `Stamp`, a private type that attaches functions for the operands of `-` and `+`, but no operators.
let private stamp =
    "type Stamp = private int\n"
    + "def Stamp.since: Stamp, Stamp -> Time(int) = { Stamp(a), Stamp(b) -> (a - b) * 1ns }\n"
    + "def Stamp.plus: Stamp, Time(int) -> Stamp = { Stamp(a), t -> Stamp(a + t // 1ns) }\n"
    + "def Stamp.minus: Stamp, Time(int) -> Stamp = { Stamp(a), t -> Stamp(a - t // 1ns) }\n"

[<Fact>]
let ``An operator a private type has none of names what it attaches for the operands instead`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'Stamp' has no `-`: it attaches `since` for these operands, as in `x \\since(y)`", "a - b"),
        onlyDiagnostic (withFinalNewline (stamp + "def f = { a: Stamp, b: Stamp -> a - b }")))

[<Fact>]
let ``Beside a measured value, an operator a private type has none of names what it attaches for the two`` () =
    for op in [ "+"; "-" ] do
        Assert.Equal(
            (DiagnosticCodes.TypeMismatch.Code,
             $"'Stamp' has no `{op}`: it attaches `minus` and `plus` for these operands, as in `x \\minus(y)` or `x \\plus(y)`",
             $"a {op} 5s"),
            onlyDiagnostic (withFinalNewline (stamp + $"def f = {{ a: Stamp -> a {op} 5s }}")))

[<Fact>]
let ``An operator a private type attaches is the one called`` () =
    let source = money + "def Money.`-`: Money, Money -> Money = { Money(x), Money(y) -> Money(x - y) }\n\ndef f = { a: Money, b: Money -> a - b }\n"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.Equal("Money, Money -> Money", typeIn result.Typed.Value "f")
