module Nanyx.Compiler.Tests.Features.Types.Annotations

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck rejects an annotation more general than its body`` () =
    let result = Compiler.compile "def f: a -> b = { x -> x }"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("more general than the inferred type", message)
    Assert.Contains("`b` would have to be `a`", message)

[<Fact>]
let ``Typecheck rejects an annotation variable the body makes concrete`` () =
    let result = Compiler.compile "def g: a -> int = { x -> x + 1 }"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("`a` would have to be `int`", message)

[<Fact>]
let ``Typecheck rejects a non-function annotation more general than its value`` () =
    let result = Compiler.compile "def xs: list(a) = [1, 2]"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("`a` would have to be `int`", message)

[<Fact>]
let ``Typecheck accepts annotations the body proves`` () =
    let source =
        "def id: a -> a = { x -> x }\n" +
        "def first: (a, b) -> a = { x, y -> x }\n" +
        "def apply: (a -> b), a -> b = { f, x -> f(x) }\n" +
        "def mapOption: (#some(a) | r), (a -> b) -> (#some(b) | r) = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def result = mapOption(#some(first(1, \"\")), { x -> apply(id, x) })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), typed.Types.["result"])

[<Fact>]
let ``Typecheck lets an annotation name a type declared later`` () =
    let source =
        "def favourite: Color = #green\n" +
        "type Color = #red | #green | #blue"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck rejects a lambda annotated with a non-function type`` () =
    let source = "def h: int = { x -> x }\ndef n = h + 1"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        Assert.Equal("{ x -> x }", source.Substring(span.Start, span.End - span.Start))
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Typecheck rejects a local lambda annotated with a non-function type`` () =
    let source = "def main = {\n  def h: int = { x -> x }\n  h + 1\n}"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        Assert.Equal("{ x -> x }", source.Substring(span.Start, span.End - span.Start))
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Typecheck rejects a lambda annotated with a type variable alone`` () =
    match (Compiler.compile "def h: a = { x -> x }").Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.AnnotationTooGeneral.Code, diagnostic.Code)
        Assert.Contains("more general than the inferred type", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Typecheck checks a lambda against the function a nominal type wraps`` () =
    let source = "type $Handler = int -> int\ndef h: $Handler = { x -> x ++ \"a\" }"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Typecheck accepts a lambda that matches its annotation`` () =
    let typed =
        compileClean (
            "type $Handler = int -> int\n" +
            "def h: $Handler = { x -> x + 1 }\n" +
            "def g: int -> int = { x -> x + 1 }\n" +
            "def n = g(2)")
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])
