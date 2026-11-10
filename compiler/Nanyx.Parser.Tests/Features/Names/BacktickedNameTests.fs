module ParserTests.Features.BacktickedNames

open Xunit
open Parser.Program
open ParserTestHelpers

let private parsed (source: string) =
    match parseModule source with
    | Result.Ok module' -> module' |> List.map (fun item -> item.Kind)
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A definition can be named by a sentence in backticks`` () =
    match parsed "def `adds two points` = { 1 }" with
    | [ Def (ValueDef (_, "adds two points", None, _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A name in backticks can hold punctuation and a keyword`` () =
    match parsed "def `is it empty?` = 1\ndef `match` = 2\ndef `it's (nearly) done, 100%!` = 3" with
    | [ Def (ValueDef (_, "is it empty?", _, _)); Def (ValueDef (_, "match", _, _)); Def (ValueDef (_, "it's (nearly) done, 100%!", _, _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A name in backticks is a value, can be called, and can name a field`` () =
    match parsed "def f = `add points`\ndef g = `add points`(1, 2)\ndef h = (`first name` = \"Ada\")\ndef i = h.`first name`" with
    | [ Def (ValueDef (_, "f", _, Kind (IdentifierExpr "add points")))
        Def (ValueDef (_, "g", _, Kind (FunctionCall ("add points", _, _))))
        Def (ValueDef (_, "h", _, Kind (RecordExpr [ NamedField ("first name", _) ])))
        Def (ValueDef (_, "i", _, Kind (MemberAccess (_, "first name", _)))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A name in backticks cannot hold a dot or a line break, or be blank`` () =
    for source in [ "def `version 1.2` = 1"; "def `two\nlines` = 1"; "def ` ` = 1" ] do
        Assert.True(parseModule source |> isError, $"Expected '{source}' not to parse")
    match parseModule "def ` ` = 1" with
    | Result.Error message -> Assert.Contains("cannot be blank", string message)
    | Result.Ok other -> failwith $"Expected a blank name not to parse, got %A{other}"

[<Fact>]
let ``The name a sentence definition declares spans its backticks`` () =
    let source = "def `adds points` = 1"
    match parseModuleFileWithNames "t.nyx" source with
    | Result.Ok (_, declared, _) ->
        let spans = declared |> Map.toList |> List.collect snd |> List.filter (fun (name, _) -> name = "adds points")
        match spans with
        | [ _, span ] -> Assert.Equal("`adds points`", source.Substring(span.Start, span.End - span.Start))
        | other -> failwith $"Expected one declaration of adds points, got %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
