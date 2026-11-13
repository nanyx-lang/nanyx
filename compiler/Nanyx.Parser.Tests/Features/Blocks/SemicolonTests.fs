module ParserTests.Features.Semicolons

open Xunit
open Parser.Program
open ParserTestHelpers

let private normalized (source: string) = source.ReplaceLineEndings "\n"

let private parse (source: string) = parseModuleFile "t.nyx" (normalized source)

/// The statements of the value of the module's only def, which must be a block.
let private statementsOf (source: string) =
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Block statements)))) ] -> statements
    | other -> failwith $"Expected one def whose value is a block, got %A{other}"

/// The parse error `source` fails with, and where it is reported.
let private errorOf (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Error error -> error.Message, error.Span.Start
    | Result.Ok parsed -> failwith $"Expected a parse error, got %A{parsed.Items}"

[<Fact>]
let ``Statements separated by a semicolon are the statements of the block`` () =
    let statements = statementsOf """
def f =
  def x = 1; def y = 2; x + y
"""
    match statements with
    | [ Kind (DefStatement (_, "x", _, _)); Kind (DefStatement (_, "y", _, _)); Kind (ExprStatement (Kind (BinaryOp ("+", _, _, _)))) ] -> ()
    | other -> Assert.Fail $"Unexpected statements: %A{other}"

[<Fact>]
let ``A line with semicolons is among the other lines of its block`` () =
    let statements = statementsOf """
def f =
  log(1); log(2)
  3
"""
    Assert.Equal(3, statements.Length)

[<Fact>]
let ``Each statement on a line with semicolons spans only its own text`` () =
    let source = normalized """
def f =
  log(1); 2
"""
    match statementsOf source with
    | [ first; second ] ->
        Assert.Equal("log(1)", textOf source first.Span)
        Assert.Equal("2", textOf source second.Span)
    | other -> Assert.Fail $"Unexpected statements: %A{other}"

[<Fact>]
let ``A semicolon in a one line lambda makes its body a block`` () =
    match parse "def f = { x -> log(x); x + 1 }\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([ "x", None ], Kind (Block [ Kind (ExprStatement (Kind (FunctionCall ("log", _, _)))); Kind (ExprStatement (Kind (BinaryOp ("+", _, _, _)))) ]), _))))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A one line lambda body can start with a def`` () =
    match parse "def f = { x -> def y = x + 1; y * 2 }\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (DefStatement (_, "y", _, _)); Kind (ExprStatement _) ]), _))))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``Both statements after a match arm's arrow are the arm's`` () =
    let source = """
def f = { x ->
  match x
    | 0 -> log(x); x
    | _ -> x
}
"""
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, [ _, Kind (Block [ _; _ ]); _, Kind (IdentifierExpr "x") ])))) ]), _))))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A statement after a semicolon in a then branch is part of the branch`` () =
    let statements = statementsOf """
def f =
  if c then a; b
"""
    match statements with
    | [ Kind (ExprStatement (Kind (IfExpr (_, Kind (Block [ _; Kind (ExprStatement (Kind (IdentifierExpr "b"))) ]), _)))) ] -> ()
    | other -> Assert.Fail $"Unexpected statements: %A{other}"

[<Fact>]
let ``Each branch of an if takes the statements after its own semicolons`` () =
    let statements = statementsOf """
def f =
  if c then a; b else d; e
"""
    match statements with
    | [ Kind (ExprStatement (Kind (IfExpr (_, Kind (Block [ _; _ ]), Kind (Block [ _; Kind (ExprStatement (Kind (IdentifierExpr "e"))) ]))))) ] -> ()
    | other -> Assert.Fail $"Unexpected statements: %A{other}"

[<Fact>]
let ``A body without a semicolon is still just its expression`` () =
    match parse "def f = { x -> x + 1 }\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (BinaryOp ("+", _, _, _)), _))))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A semicolon at the end of a line is an error at the semicolon`` () =
    let message, position = errorOf """
def f =
  log(1);
  2
"""
    Assert.Contains("A `;` must have a statement after it", message)
    Assert.Equal(("\ndef f =\n  log(1)").Length, position)

[<Fact>]
let ``A semicolon at the end of a lambda is an error`` () =
    let message, _ = errorOf "def f = { x -> log(x); }\n"
    Assert.Contains("A `;` must have a statement after it", message)

[<Fact>]
let ``Two semicolons in a row are an error at the second`` () =
    let message, position = errorOf """
def f =
  log(1);; 2
"""
    Assert.Contains("Two `;` in a row", message)
    Assert.Equal(("\ndef f =\n  log(1);").Length, position)

[<Fact>]
let ``A semicolon between top-level definitions is an error`` () =
    let source = "def x = 1; def y = 2\n"
    let _, position = errorOf source
    Assert.Equal(source.IndexOf ';', position)
