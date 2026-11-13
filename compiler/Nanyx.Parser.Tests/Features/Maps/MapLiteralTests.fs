module ParserTests.Features.Maps

open Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``[k => v, ...] is a call to __map with a list of the entries as tuples`` () =
    match parseModule """def m = ["a" => 1, "b" => f(2)]""" with
    | Result.Ok [ Kind (Def (ValueDef (_, "m", _, Kind (MapSyntax.Call [ (Kind (LiteralExpr (StringLit "a")), Kind (LiteralExpr (IntLit64 1L))); (Kind (LiteralExpr (StringLit "b")), Kind (FunctionCall ("f", _, _))) ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A map literal's entries can be written one per line`` () =
    match parseModule "def m = [\n  1 => #one\n  2 => #two\n]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "m", _, Kind (MapSyntax.Call [ (_, Kind (TagExpr ("one", None))); (_, Kind (TagExpr ("two", None))) ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A list literal without entries is still a list`` () =
    match parseModule "def xs = [1, 2]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "xs", _, Kind (ListExpr [ _; _ ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A literal of entries and elements together does not parse`` () =
    match parseModule """def m = ["a" => 1, 2]""" with
    | Result.Error err -> Assert.Contains("a map literal's items are all entries, written `key => value`", err)
    | Result.Ok other -> failwith $"Expected a parse error, got %A{other}"
