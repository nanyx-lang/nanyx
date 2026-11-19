module Nanyx.Compiler.Tests.Features.UnknownTagArms

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``An arm for a tag a closed union doesn't have is an error at its pattern, suggesting the tag meant`` () =
    let source =
        withFinalNewline """def o: #some(int) | #none = #none

def r =
  match o
    | #some(n) -> n
    | #none -> 0
    | #nothing -> 2"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal("NYX0201", code)
    Assert.Equal(
        "This arm can never match: the value is a '#some(int) | #none', whose tags are #some and #none, so it has no #nothing: did you mean #none?",
        message)
    Assert.Equal("#nothing", text)

[<Fact>]
let ``The error's fix writes the tag suggested in place of the tag's name`` () =
    let source =
        withFinalNewline """def o: #some(int) | #none = #none

def r =
  match o
    | #somme(n) -> n
    | #none -> 0"""
    let diagnostic = single (Compiler.compile source)
    let fix = Assert.Single(diagnostic.Fixes)
    let edit = Assert.Single(fix.Edits)
    Assert.Equal("#some", edit.NewText)
    Assert.Equal("#somme", textAt source edit.Span)

[<Fact>]
let ``An arm for a tag a named union doesn't have is an error`` () =
    let source =
        withFinalNewline """type T = #a(int) | #b

def t: T = #b

def r =
  match t
    | #a(n) -> n
    | #b -> 0
    | #c -> 1"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal("NYX0201", code)
    Assert.StartsWith("This arm can never match: the value is a 'T', whose tags are #a and #b, so it has no #c", message)
    Assert.Equal("#c", text)

[<Fact>]
let ``An arm of a lambda of match arms with a written type is checked against the union`` () =
    let source = withFinalNewline """type T = #a(int) | #b

def f: T -> int = { | #a(n) -> n | #b -> 0 | #c -> 1 }"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal("NYX0201", code)
    Assert.Contains("so it has no #c", message)
    Assert.Equal("#c", text)

[<Fact>]
let ``A tag a payload's union doesn't have is an error too`` () =
    let source =
        withFinalNewline """def o: #ok(#a | #b) | #none = #none

def r =
  match o
    | #ok(#c) -> 1
    | _ -> 0"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal("NYX0201", code)
    Assert.StartsWith("This arm can never match: the value is a '#a | #b', whose tags are #a and #b, so it has no #c", message)
    Assert.Equal("#c", text)

[<Fact>]
let ``A match on an open union accepts an arm for a tag it doesn't name`` () =
    let result =
        Compiler.compile (
            withFinalNewline """def g = { x ->
  match x
    | #some(n) -> n
    | #nothing -> 1
    | _ -> 0
}

def one = g(#nothing)
def two = g(#some(2))""")
    Assert.Empty(result.Diagnostics)
