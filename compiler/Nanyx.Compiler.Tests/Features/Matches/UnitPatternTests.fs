module Nanyx.Compiler.Tests.Features.UnitPatterns

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``#some() matches a tag carrying (), and () the unit value`` () =
    let save = "def save: int -> #some() | #failed = { n -> if n > 0 then #some() else #failed }\n"
    let both = save + "def f: int -> string = { n -> match save(n)\n  | #some() -> \"saved\"\n  | #failed -> \"failed\"\n}\n"
    Assert.Empty((Compiler.compile both).Diagnostics)
    let unit = "def g: () -> int = { u -> match u\n  | () -> 1\n}\n"
    Assert.Empty((Compiler.compile unit).Diagnostics)
    // `#some()` covers `#some` whole, so leaving out `#failed` is the only gap.
    let partial = save + "def f: int -> string = { n -> match save(n)\n  | #some() -> \"saved\"\n}\n"
    let diagnostic = Assert.Single((Compiler.compile partial).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Contains("failed", diagnostic.Message)

[<Fact>]
let ``() only matches the unit type`` () =
    let onInt = "def f: int -> int = { n -> match n\n  | () -> 1\n}\n"
    let diagnostic = Assert.Single((Compiler.compile onInt).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    let onPayload = "def get: int -> #some(int) | #failed = { n -> #some(n) }\ndef f = { n -> match get(n)\n  | #some() -> 1\n  | #failed -> 0\n}\n"
    let diagnostics = (Compiler.compile onPayload).Diagnostics
    Assert.NotEmpty(diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostics.Head.Code)
