module ParserTests.Features.PipePrecedence

open Xunit
open Parser.Program

// A pipe binds tighter than every binary operator (#986).

let private valueOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | other -> failwith $"Expected a single def, got: %A{other}"

[<Fact>]
let ``an operator may follow a pipe, and takes the pipe as its left operand`` () =
    match (valueOf """def v = [p.r, p.g, p.b] \min ?? 0.0""").Kind with
    | BinaryOp ("??", _, Kind (Pipe (Kind (ListExpr _), "min", _, [])), Kind (LiteralExpr _)) -> ()
    | other -> failwith $"Unexpected value: %A{other}"
    match (valueOf """def v = s \trim == "" """).Kind with
    | BinaryOp ("==", _, Kind (Pipe (Kind (IdentifierExpr "s"), "trim", _, [])), _) -> ()
    | other -> failwith $"Unexpected value: %A{other}"
    match (valueOf """def v = x \f + 1""").Kind with
    | BinaryOp ("+", _, Kind (Pipe (Kind (IdentifierExpr "x"), "f", _, [])), _) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``a pipe after an operator pipes only the operand before it`` () =
    match (valueOf """def v = a + b \f""").Kind with
    | BinaryOp ("+", _, Kind (IdentifierExpr "a"), Kind (Pipe (Kind (IdentifierExpr "b"), "f", _, []))) -> ()
    | other -> failwith $"Unexpected value: %A{other}"
    match (valueOf """def v = (a + b) \f""").Kind with
    | Pipe (Kind (BinaryOp ("+", _, _, _)), "f", _, []) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``a pipe's span starts at its operand`` () =
    let source = """def v = a + b \f(1) * 2"""
    match (valueOf source).Kind with
    | BinaryOp ("+", _, _, Kind (BinaryOp ("*", _, (Kind (Pipe _) as pipe), _))) ->
        Assert.Equal("""b \f(1)""", source.Substring(pipe.Span.Start, pipe.Span.End - pipe.Span.Start))
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``a prefix operator is part of the value it pipes`` () =
    match (valueOf """def v = -x \abs""").Kind with
    | Pipe (Kind (FunctionCall (negate, _, [ _ ])), "abs", _, []) when negate = NegateSyntax.builtIn -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``a range without an end is piped whole`` () =
    match (valueOf """def v = n + 1.. \take(3)""").Kind with
    | Pipe (Kind (FunctionCall (range, _, [ Kind (BinaryOp ("+", _, _, _)) ])), "take", _, _) when range = RangeSyntax.builtIn -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``an operator may follow a pipe in a block and on a pipe's own line`` () =
    let source =
        """def f = { xs ->
  def n = xs \length == 0
  xs
    \map { x -> x + 1 }
    \sum + n
}"""
            .ReplaceLineEndings "\n"
    match (valueOf source).Kind with
    | Lambda (_, Kind (Block [ Kind (DefStatement _); Kind (ExprStatement (Kind (BinaryOp ("+", _, Kind (Pipe (_, "sum", _, _)), _)))) ]), _) -> ()
    | other -> failwith $"Unexpected value: %A{other}"
