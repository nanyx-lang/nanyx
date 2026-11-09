module Nanyx.Compiler.Tests.Features.Attributes

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Entry attribute appends a call to the entry point`` () =
    let source =
        "module main\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"Hello, World!\")\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let entryCalls =
        result.Typed.Value.Module
        |> List.filter (function Kind (Expr (Kind (FunctionCall("main", _, [])))) -> true | _ -> false)
    Assert.Equal(1, entryCalls.Length)
    let hasAttribute = result.Typed.Value.Module |> List.exists (function Kind (Attribute _) -> true | _ -> false)
    Assert.False(hasAttribute)

[<Fact>]
let ``Unknown attribute is an error, reported at the attribute`` () =
    let result = Compiler.compile "def g = 2\n@inline\ndef f = { 1 }"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("Unknown attribute '@inline'", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((10, 17, 1, 0), (span.Start, span.End, span.StartLine, span.StartColumn))

[<Fact>]
let ``Entry attribute must precede a definition, and is reported at the attribute`` () =
    let result = Compiler.compile "@entry\n1 + 1"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("must be followed by a definition", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((0, 6), (span.Start, span.End))
