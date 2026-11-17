/// Table literals, `[| name | age | ... |]`: a list of records written as a header and rows (#1255).
module ParserTests.Features.Tables

open Xunit
open Parser.Program
open ParserTestHelpers

/// A def's value written on the lines after its `=`, which is a block of that one expression.
let private (|Below|_|) (value: Expression) =
    match value.Kind with
    | Block [ { Kind = ExprStatement single } ] -> Some single
    | _ -> None

let private namesAndAges =
    normalized
        """def namesAndAges =
  [| name     | age |
   | "Alex"   | 22  |
   | "Taylor" | 30  |
   | "Sam"    | 28  |
   | "Morgan" | 45  |]"""

[<Fact>]
let ``A table literal is a list of records, one for each row, with the header's names as their fields`` () =
    match parseModule namesAndAges with
    | Result.Ok [ Kind (Def (ValueDef (_, "namesAndAges", _, Below (Kind (ListExpr rows))))) ] ->
        let fields =
            rows
            |> List.map (function
                | Kind (RecordExpr [ NamedField ("name", Kind (LiteralExpr (StringLit name))); NamedField ("age", Kind (LiteralExpr (IntLit64 age))) ]) -> name, age
                | other -> failwith $"Unexpected row: %A{other}")
        Assert.Equal<(string * int64) list>([ "Alex", 22L; "Taylor", 30L; "Sam", 28L; "Morgan", 45L ], fields)
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Each record spans its row, and the list the whole table`` () =
    match parseModuleWithTrivia "t.nyx" namesAndAges with
    | Result.Ok parsed ->
        match parsed.Items with
        | [ Kind (Def (ValueDef (_, _, _, Below ({ Kind = ListExpr (first :: _) } as table)))) ] ->
            Assert.Equal("""| "Alex"   | 22  |""", textOf namesAndAges first.Span)
            Assert.StartsWith("[| name", textOf namesAndAges table.Span)
            Assert.EndsWith("""| 45  |]""", textOf namesAndAges table.Span)
        | other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err.Message}"

[<Fact>]
let ``A cell can be any expression`` () =
    match parseModule "def t = [| a | b |\n | f(x) | 1 + 2 |]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "t", _, Kind (ListExpr [ Kind (RecordExpr [ NamedField ("a", Kind (FunctionCall ("f", _, _))); NamedField ("b", Kind (BinaryOp ("+", _, _, _))) ]) ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A row with a different number of cells than the header is an error at the row`` () =
    let source =
        normalized
            """def t =
  [| name   | age |
   | "Alex" | 22  |
   | "Sam"  | 28  | 3 |]"""
    let message, at = parseErrorOf source
    Assert.Contains("This row has 3 cells, but the table's header names 2 fields", message)
    Assert.Equal("""| "Sam"  | 28  | 3 |]""", at)

[<Fact>]
let ``A table with no rows is an error`` () =
    let message, _ = parseErrorOf "def t = [| name | age |]"
    Assert.Contains("A table literal has a row of values under its header", message)
