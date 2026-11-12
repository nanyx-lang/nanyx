module ParserTests.Features.If

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers
open System.IO

[<Fact>]
let ``if-matching desugars to a match with a unit wildcard arm`` () =
    match parseModule "def x = if get(3) is #some(v) then v" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Match ([ Kind (FunctionCall ("get", _, _)) ],
                                                               [ [ Kind (TagPattern ("some", Some (Kind (IdentifierPattern "v")))) ], Kind (IdentifierExpr "v")
                                                                 [ Kind WildcardPattern ], Kind UnitExpr ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``if-matching with else uses the else as the wildcard arm`` () =
    match parseModule "def x = if p is (a, 0) then a else 0" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Match ([ Kind (IdentifierExpr "p") ],
                                                               [ [ Kind (TuplePattern [ Kind (IdentifierPattern "a"); Kind (LiteralPattern (IntLit 0L)) ]) ], Kind (IdentifierExpr "a")
                                                                 [ Kind WildcardPattern ], Kind (LiteralExpr (IntLit 0L)) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a condition without is stays a plain if, and is must end the keyword`` () =
    match parseModule "def x = if a == b then 1 else 2" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (IfExpr (Kind (BinaryOp ("==", _, _)), _, _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    match parseModule "def y = if island then 1 else 2" with
    | Result.Ok [ Kind (Def (ValueDef (_, "y", _, Kind (IfExpr (Kind (IdentifierExpr "island"), _, _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``multi-branch if desugars to nested ifs ending in the else arm`` () =
    match parseModule "def x = if | a -> 1 | b -> 2 | else -> 3" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (IfExpr (Kind (IdentifierExpr "a"), Kind (LiteralExpr (IntLit 1L)),
                                                                Kind (IfExpr (Kind (IdentifierExpr "b"), Kind (LiteralExpr (IntLit 2L)), Kind (LiteralExpr (IntLit 3L))))))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``multi-branch if arms may test patterns with is, and a missing else is unit`` () =
    let source =
        "def x = { s ->\n" +
        "  if | int.fromString(s) is #some(i) -> i\n" +
        "     | s == \"\" ->\n" +
        "       0\n" +
        "}\n"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match ([ Kind (FunctionCall _) ],
                                                                                      [ [ Kind (TagPattern ("some", Some (Kind (IdentifierPattern "i")))) ], Kind (IdentifierExpr "i")
                                                                                        [ Kind WildcardPattern ], Kind (IfExpr (Kind (BinaryOp ("==", _, _)), _, Kind UnitExpr)) ])))) ]), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``multi-branch if inside a match arm stops at its else arm`` () =
    let source =
        "def x = match n\n" +
        "  | 0 -> if | a -> 1 | else -> 2\n" +
        "  | _ -> 3\n"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Match (_, [ [ Kind (LiteralPattern (IntLit 0L)) ], Kind (IfExpr _)
                                                                    [ Kind WildcardPattern ], Kind (LiteralExpr (IntLit 3L)) ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse if feature files`` () =
    let unsupported = set [ "test_if_advanced.nyx" ]

    parseFeatureNyxFiles "If"
    |> Array.filter (fun (filePath, _) -> not (unsupported.Contains(Path.GetFileName(filePath))))
    |> Array.iter (fun (filePath, result) ->
        match result with
        | Result.Ok _ -> ()
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
    )
