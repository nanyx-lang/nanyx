module ParserTests.Features.Loops.LoopBodies

open Xunit
open Parser.Program
open ParserTestHelpers

/// The statements of `f`'s body, which is a block.
let private bodyOf (source: string) =
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block statements), _))))) ] -> statements
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The statements of a loop body's block, after which the function's body goes on with `()`.
let private loopStatements (source: string) =
    match bodyOf source with
    | [ Kind (ExprStatement loop); Kind (ExprStatement (Kind UnitExpr)) ] ->
        match loop.Kind with
        | ForSyntax.Call (_, _, _, Kind (Match (_, [ _, Kind (Block statements) ]))) -> statements
        | WhileSyntax.Call (_, Kind (Block statements)) -> statements
        | other -> failwith $"Unexpected loop: %A{other}"
    | other -> failwith $"Unexpected body: %A{other}"

let private assertDefThenPrint (statements: Statement list) =
    match statements with
    | [ Kind (DefStatement _); Kind (ExprStatement (Kind (FunctionCall ("println", _, [ Kind (IdentifierExpr "s") ])))) ] -> ()
    | other -> failwith $"Unexpected loop body: %A{other}"

// A match whose arms are on the lines after it ended the loop body, so the next statement fell outside it (#1311).
[<Theory>]
[<InlineData("")>]
[<InlineData("\n")>]
let ``A for loop's body goes on after a match whose arms are on the lines that follow`` (blankLine: string) =
    let source = $"""
def f = {{
  for [1, 2] | x ->
    def s = match x
      | 1 -> "one"
      | _ -> "other"
{blankLine}    println(s)
  ()
}}
"""
    loopStatements source |> assertDefThenPrint

[<Theory>]
[<InlineData("")>]
[<InlineData("\n")>]
let ``A while loop's body goes on after a match whose arms are on the lines that follow`` (blankLine: string) =
    let source = $"""
def f = {{
  while ready() ->
    def s = match x
      | 1 -> "one"
      | _ -> "other"
{blankLine}    println(s)
  ()
}}
"""
    loopStatements source |> assertDefThenPrint

[<Fact>]
let ``A loop's body goes on after a match with a match nested in an arm`` () =
    let source = """
def f = {
  for [1, 2] | x ->
    def s = match x
      | 1 ->
          match y
            | 0 -> "zero"
            | _ -> "one"
      | _ -> "other"
    println(s)
  ()
}
"""
    loopStatements source |> assertDefThenPrint

[<Fact>]
let ``A loop's body goes on after a match whose last arm's body is a block`` () =
    let source = """
def f = {
  while ready() ->
    def s = match x
      | 1 -> "one"
      | _ ->
          def t = "other"
          t
    println(s)
  ()
}
"""
    loopStatements source |> assertDefThenPrint
