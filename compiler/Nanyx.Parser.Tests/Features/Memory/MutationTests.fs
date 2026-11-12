module ParserTests.Features.Memory

open Xunit
open Parser.Program

[<Fact>]
let ``mut is a call to __mut that takes the rest of the expression`` () =
    match parseModule "def a = mut 0\ndef b = mut x + 1" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (FunctionCall ("__mut", None, [ Kind (LiteralExpr (IntLit 0)) ])))))
                  Kind (Def (ValueDef (_, "b", _, Kind (FunctionCall ("__mut", None, [ Kind (BinaryOp ("+", _, _)) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``:= is a call to __assign that binds looser than operators and pipes`` () =
    let source = """
def f = {
  total := total + x
  names := names \append(n)
}
"""
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ first; second ]), _))))) ] ->
        match first.Kind, second.Kind with
        | ExprStatement (Kind (FunctionCall ("__assign", Some _, [ Kind (IdentifierExpr "total"); Kind (BinaryOp ("+", _, _)) ]))),
          ExprStatement (Kind (FunctionCall ("__assign", Some _, [ Kind (IdentifierExpr "names"); Kind (Pipe (_, "append", _, _)) ]))) -> ()
        | other -> failwith $"Unexpected statements: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``An assignment can be a for loop's body and spans its target and value`` () =
    let source = "def f = for xs | x -> total := total + x"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (FunctionCall ("__for", None, [ _; Kind (Lambda (_, Kind (Match (_, [ _, body ])), _)) ]))))) ] ->
        match body.Kind with
        | AssignSyntax.Call (target, value) ->
            Assert.Equal("total := total + x", source.Substring(body.Span.Start, body.Span.End - body.Span.Start))
            Assert.Equal(IdentifierExpr "total", target.Kind)
            Assert.Equal("total + x", source.Substring(value.Span.Start, value.Span.End - value.Span.Start))
        | other -> failwith $"Unexpected body: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
