module ParserTests.Features.Ranges

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers
open System.IO

[<Fact>]
let ``a..b is a call to __range that binds looser than arithmetic`` () =
    match parseModule "def r = n - 1..n + 1" with
    | Result.Ok [ Kind (Def (ValueDef (_, "r", _, Kind (FunctionCall ("__range", None, [ Kind (BinaryOp ("-", _, _)); Kind (BinaryOp ("+", _, _)) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``for with arms is a call to __for with a pattern lambda, and -> without an element`` () =
    match parseModule "def a = for 1..3 | i -> f(i)\ndef b = for xs -> g()" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (FunctionCall ("__for", None, [ Kind (FunctionCall ("__range", _, _)); Kind (Lambda ([ (element, None) ], Kind (Match ([ Kind (IdentifierExpr scrutinee) ], [ [ Kind (IdentifierPattern "i") ], Kind (FunctionCall ("f", _, _)) ])), _)) ])))))
                  Kind (Def (ValueDef (_, "b", _, Kind (FunctionCall ("__for", None, [ Kind (IdentifierExpr "xs"); Kind (Lambda ([ _ ], Kind (FunctionCall ("g", _, _)), _)) ]))))) ] ->
        scrutinee |> should equal element
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a.. with nothing after it is a call to __range with only its start`` () =
    match parseModule "def a = 0..\ndef b = n + 1..\ndef c = 1.. \\take(3)\ndef d = f(0..)" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (FunctionCall ("__range", None, [ Kind (LiteralExpr (IntLit 0L)) ])))))
                  Kind (Def (ValueDef (_, "b", _, Kind (FunctionCall ("__range", None, [ Kind (BinaryOp ("+", _, _)) ])))))
                  Kind (Def (ValueDef (_, "c", _, Kind (Pipe (Kind (FunctionCall ("__range", None, [ _ ])), "take", _, _)))))
                  Kind (Def (ValueDef (_, "d", _, Kind (FunctionCall ("f", _, [ Kind (FunctionCall ("__range", None, [ _ ])) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``for over several iterables is a call to __for with a lambda of one element of each`` () =
    match parseModule "def a = for xs, 0.. | x, i -> f(x, i)\ndef b = for xs, ys -> g()" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (FunctionCall ("__for", None, [ Kind (IdentifierExpr "xs"); Kind (FunctionCall ("__range", _, [ _ ])); Kind (Lambda ([ (first, None); (second, None) ], Kind (Match ([ Kind (IdentifierExpr firstScrutinee); Kind (IdentifierExpr secondScrutinee) ], [ [ Kind (IdentifierPattern "x"); Kind (IdentifierPattern "i") ], _ ])), _)) ])))))
                  Kind (Def (ValueDef (_, "b", _, Kind (FunctionCall ("__for", None, [ Kind (IdentifierExpr "xs"); Kind (IdentifierExpr "ys"); Kind (Lambda ([ _; _ ], Kind (FunctionCall ("g", _, _)), _)) ]))))) ] ->
        Assert.Equal<string list>([ first; second ], [ firstScrutinee; secondScrutinee ])
        Assert.NotEqual<string>(first, second)
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``rec is a def whose value is wrapped in __rec`` () =
    match parseModule "def f = {\n  rec loop = { i -> loop(i) }\n  loop(1)\n}" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ Kind (DefStatement (false, "loop", None, Kind (FunctionCall ("__rec", None, [ Kind (Lambda _) ])))); Kind (ExprStatement _) ]), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse range feature files`` () =
    let unsupported = set [ "test_simple_range_expr.nyx" ]

    parseFeatureNyxFiles "Ranges"
    |> Array.filter (fun (filePath, _) -> not (unsupported.Contains(Path.GetFileName(filePath))))
    |> Array.iter (fun (filePath, result) ->
        match result with
        | Result.Ok _ -> ()
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
    )
