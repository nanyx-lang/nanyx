module ParserTests.Features.Attributes

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse attribute feature files`` () =
    parseFeatureNyxFiles "Attributes"
    |> Array.iter (fun (filePath, result) ->
        match result with
        | Result.Ok _ -> ()
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}")

[<Fact>]
let ``An attribute line precedes the definition it annotates`` () =
    match parseModule "@entry\ndef main = { 1 }" with
    | Result.Ok [ Kind (Attribute("entry", None)); Kind (Def (ValueDef(false, "main", None, _))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``An attribute's argument is written in parentheses straight after its name`` () =
    match parseModule "@test(#io)\ndef readsConfig = { 1 }" with
    | Result.Ok [ Kind (Attribute("test", Some (Kind (TagExpr("io", None))))); Kind (Def (ValueDef(false, "readsConfig", None, _))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``Several attribute arguments make a tuple, and named ones a record`` () =
    match parseModule "@a(1, 2)\ndef x = 1\n@b(timeout = 5)\ndef y = 2" with
    | Result.Ok [ Kind (Attribute("a", Some (Kind (TupleExpr [ _; _ ])))); Kind (Def _)
                  Kind (Attribute("b", Some (Kind (RecordExpr [ NamedField("timeout", _) ])))); Kind (Def _) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``Empty parentheses after an attribute are its unit argument`` () =
    match parseModule "@test()\ndef x = 1" with
    | Result.Ok [ Kind (Attribute("test", Some (Kind UnitExpr))); Kind (Def _) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``An attribute needs a name`` () =
    parseModule "@\ndef main = { 1 }" |> isError |> should equal true
