/// `a ?? b`: the value inside an option-like `a`'s `#some`, or the plain value `b`. It only unwraps, so a `b` that is
/// option-like is an error of its own, which points at `orElseSome`.
module Nanyx.Compiler.Tests.Features.Coalesce

open Xunit
open NyxCompiler

let private typesOf (source: string) =
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    fun name -> Unifier.tyToString result.Typed.Value.Types.[name]

[<Fact>]
let ``?? gives the type of the value inside #some`` () =
    let typeOf =
        typesOf """def lookup: string -> #some(int) | #missing = { k -> #missing }
def found = lookup("a") ?? 0
def chained = lookup("a") ?? lookup("b") ?? 0
def compared = lookup("a") ?? 0 == 1
def defaults = [lookup("a")] \list.map { ?? 0 }
def viaHelper = someOrElse(lookup("a"), { 0 })
"""
    Assert.Equal("int", typeOf "found")
    Assert.Equal("int", typeOf "chained")
    Assert.Equal("bool", typeOf "compared")
    Assert.Equal("list(int)", typeOf "defaults")
    Assert.Equal("int", typeOf "viaHelper")

[<Fact>]
let ``?? takes a nested option as its fallback when #some holds one`` () =
    let typeOf = typesOf "def nested: #some(#some(int) | #none) | #none = #none\ndef inner = nested ?? #none\n"
    Assert.Equal("#some(int) | #none", typeOf "inner")

[<Fact>]
let ``An option-like right operand of ?? is an error that points at orElseSome`` () =
    let source =
        """def lookup: string -> #some(int) | #missing = { k -> #missing }
def x = lookup("a") ?? lookup("b")
"""
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.OptionLikeFallback.Code, diagnostic.Code)
        Assert.Contains("`??` only unwraps", diagnostic.Message)
        Assert.Contains("orElseSome", diagnostic.Message)
        Assert.Equal("lookup(\"b\")", source.Substring(diagnostic.Span.Value.Start, diagnostic.Span.Value.End - diagnostic.Span.Value.Start))
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A plain right operand of the wrong type is a type mismatch`` () =
    let source = "def o: #some(int) | #none = #none\ndef x = o ?? \"none\"\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    | other -> failwith $"Expected one diagnostic, got %A{other}"
