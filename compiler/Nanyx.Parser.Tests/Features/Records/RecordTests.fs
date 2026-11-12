module ParserTests.Features.Records

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``record update with is a call to __with on the record and the updated fields`` () =
    match parseModule "def older = alice with age = 31, name = \"Al\"" with
    | Result.Ok [ Kind (Def (ValueDef (_, "older", _, Kind (FunctionCall ("__with", None, [ Kind (IdentifierExpr "alice"); Kind (RecordExpr [ NamedField ("age", Kind (LiteralExpr (IntLit 31L))); NamedField ("name", Kind (LiteralExpr (StringLit "Al"))) ]) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``with applies to a member access and does not swallow identifiers starting with with`` () =
    match parseModule "def p = team.lead with age = 1\ndef q = without" with
    | Result.Ok [ Kind (Def (ValueDef (_, "p", _, Kind (FunctionCall ("__with", None, [ Kind (MemberAccess (Kind (IdentifierExpr "team"), "lead", _)); Kind (RecordExpr [ _ ]) ])))))
                  Kind (Def (ValueDef (_, "q", _, Kind (IdentifierExpr "without")))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

let private lines (source: string list) = String.concat "\n" source

let private parsesLike (expected: string) (source: string) =
    match parseModule source, parseModule expected with
    | Result.Ok actual, Result.Ok wanted -> actual |> should equal wanted
    | Result.Error err, _ -> failwith $"Parse failed: {err}"
    | _, Result.Error err -> failwith $"Expected source failed to parse: {err}"

[<Fact>]
let ``with takes its updates in parentheses, separated by commas or one per line`` () =
    """def older = user with (age = 31, name = "Al")"""
    |> parsesLike """def older = user with age = 31, name = "Al" """
    let block =
        """
def older = user with (
  age = 31
  name = "Al"
)
"""
    block.ReplaceLineEndings "\n"
    |> parsesLike """def older = user with age = 31, name = "Al" """

[<Fact>]
let ``with in parentheses nests, as the records tour writes it`` () =
    let source =
        """
def updatedLine = line with (
  range = line.range with (
    start = line.range.start with (
      character = 4
    )
  )
)
"""
    source.ReplaceLineEndings "\n"
    |> parsesLike "def updatedLine = line with range = (line.range with start = (line.range.start with character = 4))"

[<Fact>]
let ``record fields can be written one per line without commas`` () =
    lines [ "def x = ("; "  a = 1"; "  b = 2"; ")" ]
    |> parsesLike "def x = (a = 1, b = 2)"

[<Fact>]
let ``call arguments can be written one per line without commas`` () =
    lines [ "def y = R("; "  a = 1"; "  b = 2"; ")" ]
    |> parsesLike "def y = R(a = 1, b = 2)"

[<Fact>]
let ``positional fields can be written one per line without commas`` () =
    lines [ "def t = ("; "  1"; "  \"two\""; ")" ]
    |> parsesLike "def t = (1, \"two\")"

[<Fact>]
let ``commas and newlines can be mixed`` () =
    lines [ "def x = ("; "  a = 1, b = 2"; "  c = 3,"; "  d = 4"; ")" ]
    |> parsesLike "def x = (a = 1, b = 2, c = 3, d = 4)"

[<Fact>]
let ``blank lines and comments may come between fields on their own lines`` () =
    lines [ "def x = ("; "  a = 1"; ""; "  -- the second field"; "  b = 2 -- trailing"; ")" ]
    |> parsesLike "def x = (a = 1, b = 2)"

[<Fact>]
let ``a field may run over several lines`` () =
    lines
        [ "def x = ("
          "  apply = { body ->"
          "    body()"
          "  }"
          "  pick = match n"
          "    | 0 -> \"zero\""
          "    | _ -> \"many\""
          "  last = 3"
          ")" ]
    |> parsesLike (
        lines
            [ "def x = ("
              "  apply = { body ->"
              "    body()"
              "  },"
              "  pick = match n"
              "    | 0 -> \"zero\""
              "    | _ -> \"many\","
              "  last = 3"
              ")" ])

[<Fact>]
let ``fields on their own lines nest inside an indented call`` () =
    lines
        [ "def decode = {"
          "  json.decode(text, json.record("
          "    name = json.field(\"name\", json.string)"
          "    age = json.field(\"age\", json.int)"
          "  ))"
          "}" ]
    |> parsesLike "def decode = {\n  json.decode(text, json.record(name = json.field(\"name\", json.string), age = json.field(\"age\", json.int)))\n}"

[<Fact>]
let ``a field on a line indented differently from the first field needs a comma`` () =
    lines [ "def x = ("; "  a = 1"; "    b = 2"; ")" ]
    |> parseModule
    |> isError
    |> should equal true

[<Fact>]
let ``fields are not aligned to the bracket when the first one shares its line`` () =
    lines [ "def y = R(a = 1"; "          b = 2)" ]
    |> parseModule
    |> isError
    |> should equal true

[<Fact>]
let ``Parse record feature files`` () =
    parseFeatureNyxFiles "Records"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )
