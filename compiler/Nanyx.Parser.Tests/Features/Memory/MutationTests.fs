module ParserTests.Features.Memory

open Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``mut declares a cell: a def whose value is a call to __mut spanning the declaration`` () =
    let source = """
def f = {
  mut a = 0
  mut b: int = x + 1
}
"""
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ first; second ]), _))))) ] ->
        match first.Kind, second.Kind with
        | DefStatement (false, "a", None, (Kind (FunctionCall ("__mut", None, [ Kind (LiteralExpr (IntLit64 0L)) ])) as value)),
          DefStatement (false, "b", Some _, Kind (FunctionCall ("__mut", None, [ Kind (BinaryOp ("+", _, _, _)) ]))) ->
            Assert.Equal("mut a = 0", source.Substring(value.Span.Start, value.Span.End - value.Span.Start))
        | other -> failwith $"Unexpected statements: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``mut is not an expression, so def x = mut e is an error that says to write mut x = e`` () =
    let message, at = parseErrorOf "def f = {\n  def x = mut 0\n  x\n}\n"
    Assert.Contains("`mut count = 0` in place of `def count = mut 0`", message)
    Assert.Equal("mut 0", at)

[<Fact>]
let ``mut declares one name, so a pattern after it is an error`` () =
    for pattern in [ "(a, b)"; "(x = a)"; "#some(v)" ] do
        let message, at = parseErrorOf $"def f = {{\n  mut {pattern} = value\n  0\n}}\n"
        Assert.Contains("`mut` declares one cell, by name", message)
        Assert.Equal($"{pattern} = value", at)
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
        | ExprStatement (Kind (FunctionCall ("__assign", Some _, [ Kind (IdentifierExpr "total"); Kind (BinaryOp ("+", _, _, _)) ]))),
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


[<Fact>]
let ``mut on a parameter takes a cell: its annotation is mut _, with no source of its own`` () =
    let source = "def f = { mut cell, mut held: int, n -> n }"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([ ("cell", cell); ("held", held); ("n", None) ], _, _))))) ] ->
        match cell, held with
        | CellPassingSyntax.CellParameter None, CellPassingSyntax.CellParameter (Some { Kind = TypeName "int" }) -> ()
        | other -> failwith $"Unexpected parameters: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``mut on a parameter takes one name, so a pattern after it is an error`` () =
    let message, at = parseErrorOf "def f = { mut (a, b) -> a }\n"
    Assert.Contains("`mut` on a parameter takes a single name", message)
    Assert.Equal("(a, b) -> a }", at)

[<Fact>]
let ``mut x as an argument passes the cell: a call to __mut_arg spanning both`` () =
    let source = "def f = swap(mut a, mut b)"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (FunctionCall ("swap", _, [ Kind (TupleExpr [ first; Kind (CellPassingSyntax.Argument (Kind (IdentifierExpr "b"))) ]) ]))))) ] ->
        match first.Kind with
        | CellPassingSyntax.Argument (Kind (IdentifierExpr "a")) -> Assert.Equal("mut a", source.Substring(first.Span.Start, first.Span.End - first.Span.Start))
        | other -> failwith $"Unexpected argument: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``mut T is the type of a cell`` () =
    match parseModule "def inc: [$Memory] mut int -> () = { mut cell -> () }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "inc", Some (Kind (TypeWithContext (_, Kind (TypeFunc (Kind (TypeApply ("mut", [ Kind (TypeName "int") ])), _))))), _))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"