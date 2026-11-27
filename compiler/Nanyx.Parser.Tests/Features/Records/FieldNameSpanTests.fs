/// A record field's name keeps its span, in a record literal, a record pattern, a record type and a field access, so
/// that an error about the field is reported at its name (#2718).
module ParserTests.Features.FieldNameSpans

open Xunit
open Parser.Program

/// The names `source` declares as `declarer`, each with the text of its span, in the order of their offsets.
let private declaredAs (declarer: Declarer) (source: string) =
    match parseModuleFileWithNames "t.nyx" source with
    | Result.Ok (_, declared, _) ->
        declared
        |> Map.toList
        |> List.filter (fun ((_, by), _) -> by = declarer)
        |> List.collect snd
        |> List.sortBy (fun (_, span) -> span.Start)
        |> List.map (fun (name, span) -> name, source.Substring(span.Start, span.End - span.Start))
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

[<Fact>]
let ``A record literal keeps each field name's span`` () =
    Assert.Equal<(string * string) list>([ "width", "width"; "height", "height" ], declaredAs NamedFieldValue "def p = (width = 1, height = 2)\n")

[<Fact>]
let ``A record pattern keeps each field name's span`` () =
    let source = "def f = { p ->\n  match p\n    | (width = w, height = h) -> w\n}\n"
    Assert.Equal<(string * string) list>([ "width", "width"; "height", "height" ], declaredAs NamedFieldValue source)

[<Fact>]
let ``A record type keeps each field name's span`` () =
    Assert.Equal<(string * string) list>([ "width", "width"; "height", "height" ], declaredAs NamedFieldValue "type P = (width: int, height: int)\n")

[<Fact>]
let ``A field access keeps the field name's span`` () =
    let source = "def h = p.height\n"
    match parseModuleFile "t.nyx" source with
    | Result.Ok [ Kind (Def (ValueDef(_, _, _, { Kind = MemberAccess(_, "height", Some span) }))) ] ->
        Assert.Equal("height", source.Substring(span.Start, span.End - span.Start))
    | other -> failwith $"Expected a field access, got %A{other}"
