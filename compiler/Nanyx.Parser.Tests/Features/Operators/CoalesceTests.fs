/// `a ?? b`: the value inside `a`'s `#some`, or `b`. It binds tighter than the comparisons and looser than
/// composition, and groups to the right.
module ParserTests.Features.Coalesce

open Xunit
open Parser.Program
open ParserTestHelpers

/// The value of `def x = ...` in `source`, with each operator in brackets around its operands.
let private grouped (source: string) =
    let rec show (expr: Expression) =
        match expr.Kind with
        | BinaryOp(op, _, left, right) -> $"({show left} {op} {show right})"
        | IdentifierExpr name -> name
        | LiteralExpr (IntLit n) -> string n
        | FunctionCall(name, _, args) -> $"""{name}({args |> List.map show |> String.concat ", "})"""
        | other -> failwith $"Unexpected expression: %A{other}"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, value))) ] -> show value
    | other -> failwith $"Expected one def of x, got %A{other}"

[<Theory>]
[<InlineData("def x = a ?? b", "(a ?? b)")>]
[<InlineData("def x = a??b", "(a ?? b)")>]
[<InlineData("def x = a ?? b ?? c", "(a ?? (b ?? c))")>]
[<InlineData("def x = a ?? b == c", "((a ?? b) == c)")>]
[<InlineData("def x = a ?? 0 < n", "((a ?? 0) < n)")>]
[<InlineData("def x = p and a ?? b", "(p and (a ?? b))")>]
[<InlineData("def x = a ?? b or c", "((a ?? b) or c)")>]
[<InlineData("def x = a ?? b + 1", "(a ?? (b + 1))")>]
[<InlineData("def x = a ?? b ++ c", "(a ?? (b ++ c))")>]
[<InlineData("def x = f(a) ?? g(b)", "(f(a) ?? g(b))")>]
let ``?? groups to the right, tighter than comparison and looser than arithmetic`` (source: string) (expected: string) =
    Assert.Equal(expected, grouped source)

[<Fact>]
let ``?? does not take the start of the placeholder ???`` () =
    match parseModule "def f = { o ->\n  o ?? ???\n}" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda(_, Kind (Block [ Kind (ExprStatement (Kind (BinaryOp("??", _, _, _)))) ]), _))))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``?? alone in braces is a shorthand lambda of two operands`` () =
    match parseModule "def x = { ?? }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Lambda([ _; _ ], Kind (BinaryOp("??", _, _, _)), _))))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``?? cannot be defined, as it is syntax`` () =
    let reported, text = parseErrorOf "def `??` = { a, b -> a }"
    Assert.Contains("`??` is built in, so it cannot be defined", reported)
    Assert.Equal("`??` = { a, b -> a }", text)
