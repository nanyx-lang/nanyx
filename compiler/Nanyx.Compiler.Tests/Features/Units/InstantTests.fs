/// `Instant`, a point in time (#2626): it is moved and measured with named functions, not operators, and `Clock`
/// gives one.
module Nanyx.Compiler.Tests.Features.Instant

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``The time since an earlier now is a Time(int), and ticks are one`` () =
    let source = "def took: [Clock] Instant -> Time(int) = { start -> now() \\since(start) }\ndef timed: [Clock] () -> Time(int) = { _ -> ticks() - ticks() }"
    Assert.Empty((Compiler.compile (withFinalNewline source)).Diagnostics)

[<Fact>]
let ``An instant is moved by a time and measured from another`` () =
    let result = Compiler.compile (withFinalNewline "def a = Instant.epoch \\plus(5s)\ndef b = a \\minus(1d)\ndef c = a \\since(b)")
    Assert.Empty(result.Diagnostics)
    Assert.Equal<string list>(
        [ "Instant"; "Instant"; "Time(int)" ],
        [ "a"; "b"; "c" ] |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types[name]))

[<Fact>]
let ``A time added to or taken from an instant with an operator names what Instant attaches for the two`` () =
    for op in [ "+"; "-" ] do
        Assert.Equal(
            (DiagnosticCodes.TypeMismatch.Code,
             $"'Instant' has no `{op}`: it attaches `minus` and `plus` for these operands, as in `x \\minus(y)` or `x \\plus(y)`",
             $"a {op} 5s"),
            onlyDiagnostic (withFinalNewline $"def f = {{ a: Instant -> a {op} 5s }}"))
