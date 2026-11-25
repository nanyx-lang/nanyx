module ParserTests.Features.FloorDivision

open Xunit
open Parser.Program
open ParserTestHelpers

// #2716: `//` divides to a whole number. It is one token, not a comment, of the precedence of `*`, `/` and `%`.

[<Fact>]
let ``// is one operator`` () =
    match parseModule "def x = a // b" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("//", _, Kind (IdentifierExpr "a"), Kind (IdentifierExpr "b")))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// `expr` with each binary operator in parentheses: `((a * b) // c)`.
let rec private grouped (expr: Expression) =
    match expr.Kind with
    | BinaryOp(op, _, left, right) -> $"({grouped left} {op} {grouped right})"
    | IdentifierExpr name -> name
    | other -> failwith $"Unexpected expression: %A{other}"

[<Fact>]
let ``// groups to the left with *, / and %, and binds tighter than +`` () =
    match parseModule "def x = a * b // c % d / e + f" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, value))) ] -> Assert.Equal("(((((a * b) // c) % d) / e) + f)", grouped value)
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``// can be attached to a type`` () =
    Assert.True(isDefinableOperator "//")
    match parseModule "def Money.`//` = { x, y -> x }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "Money.//", _, _))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
