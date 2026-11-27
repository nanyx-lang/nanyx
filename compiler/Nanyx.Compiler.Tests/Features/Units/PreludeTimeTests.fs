/// The prelude's `Time` and its units (#2625): every module sees them, a module's own replace them, and `sleep` takes a
/// `Time(int)`.
module Nanyx.Compiler.Tests.Features.PreludeTime

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Every module sees the prelude's Time and its units`` () =
    Assert.Equal<string list>(
        [ "Time(int)"; "Time(float)"; "int" ],
        typesOf (withFinalNewline "def a = 1ns + 1us + 1ms + 1s + 1min + 1hr + 1d\ndef b = 0.5s\ndef c = 1d // 1hr") [ "a"; "b"; "c" ])

[<Fact>]
let ``A module's own dimension Time and unit s are the ones their names mean there`` () =
    let result = Compiler.compile (withFinalNewline "dimension Time(float)\n@baseUnit unit s: Time\ndef a = 2s")
    Assert.Empty(result.Diagnostics)
    Assert.Equal("Time(float)", Unifier.tyToString result.Typed.Value.Types["a"])
    Assert.Equal("1", Rational.text result.Typed.Value.Units["s"].Scale)

[<Fact>]
let ``A value named ms is still a value beside the unit`` () =
    Assert.Equal<string list>([ "int"; "Time(int)" ], typesOf (withFinalNewline "def ms = 3\ndef a = ms + 1\ndef b = 500ms") [ "a"; "b" ])

[<Fact>]
let ``sleep takes a time, written with any unit, and not a plain number`` () =
    let sleeps (arg: string) = $"def nap: [Async, Clock] () -> () = {{ _ -> sleep({arg}) }}"
    for arg in [ "500ms"; "0.5s"; "1min" ] do
        Assert.Empty((Compiler.compile (withFinalNewline (sleeps arg))).Diagnostics)
    let code, _, at = onlyDiagnostic (withFinalNewline (sleeps "500"))
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("500", at)
