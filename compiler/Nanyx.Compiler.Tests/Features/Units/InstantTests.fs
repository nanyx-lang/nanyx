/// `Instant`, a point in time (#2626): it is moved by a time and measured from another, with `+` and `-` or by name
/// (#2874), and `Clock` gives one.
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
let ``The time between two instants is their difference, and a time added to one moves it`` () =
    let source = "def took: [Clock] Instant -> Time(int) = { start -> now() - start }\ndef a = Instant.epoch + 5s\ndef c = a - Instant.epoch"
    let result = Compiler.compile (withFinalNewline source)
    Assert.Empty(result.Diagnostics)
    Assert.Equal<string list>(
        [ "Instant -> Time(int)"; "Instant"; "Time(int)" ],
        [ "took"; "a"; "c" ] |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types[name]))

[<Fact>]
let ``A time taken from an instant is by name, as its - is the time between two`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'Time(int)' vs 'Instant'", "a - 5s"),
        onlyDiagnostic (withFinalNewline "def f = { a: Instant -> a - 5s }"))
