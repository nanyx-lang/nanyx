module ParserTests.Features.CallResult

open Xunit
open Parser.Program
open ParserTestHelpers

// A call on a call's result, `f()(args)`, is a block that calls the result by a made-up name (see `CallResultSyntax`).

/// The value of the one def in `source`.
let private valueOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A call on a call's result calls the result`` () =
    match (valueOf "def h = g()()").Kind with
    | CallResultSyntax.Call (Kind (FunctionCall ("g", _, [])), Kind (FunctionCall (_, _, []))) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A call on a call's result passes its own arguments`` () =
    match (valueOf "def h = add(1)(2)").Kind with
    | CallResultSyntax.Call (Kind (FunctionCall ("add", _, [ Kind (LiteralExpr (IntLit first)) ])), Kind (FunctionCall (_, _, [ Kind (LiteralExpr (IntLit second)) ]))) ->
        Assert.Equal((1I, 2I), (first, second))
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``Calls on call results nest, each spanning from the start of the first`` () =
    let source = "def h = f(1)(2)(3)"
    let value = valueOf source
    Assert.Equal("f(1)(2)(3)", source.Substring(value.Span.Start, value.Span.End - value.Span.Start))
    match value.Kind with
    | CallResultSyntax.Call (({ Kind = CallResultSyntax.Call (Kind (FunctionCall ("f", _, _)), _) } as inner), _) ->
        Assert.Equal("f(1)(2)", source.Substring(inner.Span.Start, inner.Span.End - inner.Span.Start))
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A call on a call's result is called in a statement and in an interpolation hole`` () =
    let source =
        """
def main = {
  mk()()
  println("{add(1)(2)}")
}
"""
    match (valueOf (source.ReplaceLineEndings "\n")).Kind with
    | Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (CallResultSyntax.Call _))); Kind (ExprStatement (Kind (FunctionCall ("println", _, [ Kind (InterpolatedString [ StringExpr (Kind (CallResultSyntax.Call _)) ]) ])))) ]), _) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
