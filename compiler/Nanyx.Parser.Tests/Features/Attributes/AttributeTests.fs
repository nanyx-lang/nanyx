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
let ``A def with nothing after its type is a foreign function's declaration, whose value is a call to __extern`` () =
    let source = "@extern(\"wasi_snapshot_preview1\", \"proc_exit\")\ndef procExit: [HostRuntime] I32 -> () -- ends\ndef next = 1"
    match parseModule source with
    | Result.Ok [ Kind (Attribute("extern", Some _)); Kind (Def (ValueDef(false, "procExit", Some _, value))); Kind (Def (ValueDef(false, "next", None, _))) ] ->
        match value.Kind with
        | ExternSyntax.Bodiless -> Assert.Equal((47, 84), (value.Span.Start, value.Span.End))
        | other -> failwith $"Unexpected value: %A{other}"
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A def with something other than a value after its type is still an error`` () =
    parseModule "def x: int 5" |> isError |> should equal true

[<Fact>]
let ``An attribute before a context's member is a field attribute before the member`` () =
    let source =
        """context Async = (
  @customKeywordType(#bind)
  await: [c] (int, ([c] int -> int)) -> int
  zero: int
)"""
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (TypeDef("Async", _, [], Kind (TypeRecord fields)))) ] ->
        match fields with
        | [ TypeFieldAttribute("customKeywordType", Some (Kind (TagExpr("bind", None))), span); TypeField("await", false, Some _, None); TypeField("zero", false, Some _, None) ] ->
            Assert.Equal((20, 45), (span.Start, span.End))
        | other -> failwith $"Unexpected fields: %A{other}"
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A field can have several attributes, on its line or the lines above`` () =
    match parseModule "type T = (@a @b(1) x: int, y: int)" with
    | Result.Ok [ Kind (Def (TypeDef("T", _, [], Kind (TypeRecord [ TypeFieldAttribute("a", None, _); TypeFieldAttribute("b", Some _, _); TypeField("x", _, _, _); TypeField("y", _, _, _) ])))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``An attribute after a record type's last field is an error`` () =
    parseModule "context C = (\n  x: int\n  @customKeywordType(#bind)\n)" |> isError |> should equal true

[<Fact>]
let ``An attribute needs a name`` () =
    parseModule "@\ndef main = { 1 }" |> isError |> should equal true
