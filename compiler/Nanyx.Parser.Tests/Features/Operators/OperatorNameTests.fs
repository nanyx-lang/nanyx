module ParserTests.Features.OperatorNames

open Xunit
open Parser.Program
open ParserTestHelpers

let private parsed (source: string) =
    match parseModule source with
    | Result.Ok module' -> module' |> List.map (fun item -> item.Kind)
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A definition can be named by an operator in backticks`` () =
    match parsed "def `<+>` = { a, b -> a + b }" with
    | [ Def (ValueDef (_, "<+>", None, Kind (Lambda _))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Theory>]
[<InlineData("def `+++` = { a, b -> a }", "`+++` = { a, b -> a }", "+++")>]
[<InlineData("def $Money.`<=>` = { a, b -> a }", "`<=>` = { a, b -> a }", "<=>")>]
[<InlineData("def f = {\n  def `!` = { a -> a }\n  1\n}", "`!` = { a -> a }", "!")>]
[<InlineData("def f = {\n  rec `**!` = { a -> a }\n  1\n}", "`**!` = { a -> a }", "**!")>]
let ``An operator Nanyx does not have cannot be defined`` (source: string) (at: string) (operator: string) =
    let reported, text = parseErrorOf source
    Assert.Contains($"`{operator}` is not one of Nanyx's operators, so it cannot be defined. The operators are `**`, `*`", reported)
    Assert.Equal(at, text)

[<Fact>]
let ``An operator can be attached to a type`` () =
    match parsed "def Point.`==`: Point, Point -> bool = { p, q -> true }" with
    | [ Def (ValueDef (_, "Point.==", Some _, _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
    match parsed "def $Email.`+` = { a, b -> a }" with
    | [ Def (ValueDef (_, "$Email.+", None, _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A local definition can be named by an operator`` () =
    match parsed "def f = {\n  def `<*>` = { a, b -> a }\n  `<*>`(1, 2)\n}" with
    | [ Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ Kind (DefStatement (_, "<*>", _, _)); Kind (ExprStatement (Kind (FunctionCall ("<*>", _, _)))) ]), _)))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A context member and a record field can be named by an operator`` () =
    match parsed "context Eq(a) = (`==`: (a, a) -> bool)" with
    | [ Def (TypeDef ("Eq", _, _, Kind (TypeRecord [ TypeField ("==", false, false, Some _, None) ]))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
    match parsed "def main = {\n  def eqInt: Eq(int) = (`==` = { a, b -> true })\n  1\n}" with
    | [ Def (ValueDef (_, "main", _, Kind (Lambda ([], Kind (Block [ Kind (DefStatement (_, "eqInt", Some _, Kind (RecordExpr [ NamedField ("==", _) ]))); _ ]), _)))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``An operator in backticks is a value and can be called`` () =
    match parsed "def f = `+`\ndef g = `+`(1, 2)\ndef h = Point.`==`(p, q)\ndef i = Eq(a).`==`(x, y)" with
    | [ Def (ValueDef (_, "f", _, Kind (IdentifierExpr "+")))
        Def (ValueDef (_, "g", _, Kind (FunctionCall ("+", _, _))))
        Def (ValueDef (_, "h", _, Kind (FunctionCall ("Point.==", _, _))))
        Def (ValueDef (_, "i", _, Kind (ContextMemberCall (_, "==", _)))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A spelling that is syntax cannot be a name`` () =
    for source in [ "def `=` = 1"; "def `->` = 1"; "def `=>` = 1"; "def `--` = 1" ] do
        match parseModule source with
        | Result.Error message -> Assert.True((string message).Contains "is syntax, not an operator", string message)
        | Result.Ok other -> failwith $"Expected '{source}' not to parse, got %A{other}"

[<Fact>]
let ``The name an operator definition declares spans its backticks`` () =
    let source = "def Point.`==` = { p, q -> true }"
    match parseModuleFileWithNames "t.nyx" source with
    | Result.Ok (_, declared, _) ->
        let spans = declared |> Map.toList |> List.collect snd |> List.filter (fun (name, _) -> name = "Point.==")
        match spans with
        | [ _, span ] -> Assert.Equal("Point.`==`", source.Substring(span.Start, span.End - span.Start))
        | other -> failwith $"Expected one declaration of Point.==, got %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
