module ParserTests.Features.Matches

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers
open System.IO

[<Fact>]
let ``a braced guard pattern takes any expression and the bare form still parses`` () =
    match parseModule "def f = { | { < 0 } -> 1 | { >= limit * 2 } -> 2 | > 5 -> 3 | _ -> 4 }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Match (_, arms))))))) ] ->
        match arms |> List.map fst with
        | [ [ Kind (GuardPattern ("<", Kind (LiteralExpr (IntLit 0)))) ]
            [ Kind (GuardPattern (">=", Kind (BinaryOp ("*", Kind (IdentifierExpr "limit"), _)))) ]
            [ Kind (GuardPattern (">", Kind (LiteralExpr (IntLit 5)))) ]
            [ Kind WildcardPattern ] ] -> ()
        | other -> failwith $"Unexpected arms: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse match feature files`` () =
    let unsupported = set [ "test_match_basic.nyx"; "test_match_binding.nyx"; "test_match_guard.nyx"; "test_match_lambda.nyx" ]

    parseFeatureNyxFiles "Matches"
    |> Array.filter (fun (filePath, _) -> not (unsupported.Contains(Path.GetFileName(filePath))))
    |> Array.iter (fun (filePath, result) ->
        match result with
        | Result.Ok _ -> ()
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
    )

open Parser.Program

[<Fact>]
let ``An or-pattern arm becomes one arm per alternative`` () =
    let source =
        "def f = { day ->\n" +
        "  match day\n" +
        "    | \"Sat\" | \"Sun\" -> \"weekend\"\n" +
        "    | _ -> \"weekday\"\n" +
        "}"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, arms)))) ])))))) ] ->
        let patterns = arms |> List.map (fst >> List.map (fun pattern -> pattern.Kind))
        patterns |> should equal [ [ LiteralPattern (StringLit "Sat") ]; [ LiteralPattern (StringLit "Sun") ]; [ WildcardPattern ] ]
        // Both alternatives share the same body.
        snd arms.[0] |> should equal (snd arms.[1])
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``An or-pattern may put each alternative on its own line`` () =
    let source =
        "def f = { day ->\n" +
        "  match day\n" +
        "    | #sat\n" +
        "    | #sun -> 1\n" +
        "    | _ -> 0\n" +
        "}"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, arms)))) ])))))) ] ->
        arms
        |> List.map (fst >> List.map (fun pattern -> pattern.Kind))
        |> should equal [ [ TagPattern ("sat", None) ]; [ TagPattern ("sun", None) ]; [ WildcardPattern ] ]
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The scrutinees of the one match in `def f = { x -> <body> }`, or the parse error.
let private scrutineesOf (body: string) =
    match parseModule ("def f = { x ->\n" + body + "\n}") with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (scrutinees, _)))) ])))))) ] ->
        Result.Ok scrutinees
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> Result.Error err

[<Fact>]
let ``A scrutinee may be a pipe`` () =
    match scrutineesOf "  match s \\indexOf(separator)\n    | #some(i) -> i\n    | #none -> 0" with
    | Result.Ok [ Kind (Pipe (Kind (IdentifierExpr "s"), "indexOf", _, [ Kind (IdentifierExpr "separator") ])) ] -> ()
    | other -> failwith $"Unexpected scrutinees: %A{other}"

[<Fact>]
let ``A scrutinee may use operators, and several may be piped`` () =
    match scrutineesOf "  match a + 1, xs \\length\n    | 1, 0 -> 1\n    | _, _ -> 0" with
    | Result.Ok [ Kind (BinaryOp ("+", _, _)); Kind (Pipe (_, "length", _, [])) ] -> ()
    | other -> failwith $"Unexpected scrutinees: %A{other}"

[<Fact>]
let ``A scrutinee may be a block indented more than the match and its arms`` () =
    let body =
        "  match\n" +
        "      def trimmed = s \\trim\n" +
        "      trimmed \\split(\",\")\n" +
        "    | [a, b] -> a\n" +
        "    | _ -> \"\""
    match scrutineesOf body with
    | Result.Ok [ Kind (Block [ Kind (DefStatement _); Kind (ExprStatement (Kind (Pipe (_, "split", _, _)))) ]) ] -> ()
    | other -> failwith $"Unexpected scrutinees: %A{other}"

[<Fact>]
let ``A block scrutinee of one expression is that expression`` () =
    match scrutineesOf "  match\n      xs \\length\n    | 0 -> 1\n    | _ -> 0" with
    | Result.Ok [ Kind (Pipe (Kind (IdentifierExpr "xs"), "length", _, [])) ] -> ()
    | other -> failwith $"Unexpected scrutinees: %A{other}"

[<Fact>]
let ``A block scrutinee is measured against the match line, not the match keyword`` () =
    let source =
        "def f = { xs ->\n" +
        "  def n = match\n" +
        "      xs \\length\n" +
        "    | 0 -> 1\n" +
        "    | _ -> 0\n" +
        "  n\n" +
        "}"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (DefStatement (_, _, _, Kind (Match ([ Kind (Pipe _) ], _)))); _ ])))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A block scrutinee indented no more than the arms is an error`` () =
    match scrutineesOf "  match\n    xs \\length\n    | 0 -> 1\n    | _ -> 0" with
    | Result.Error _ -> ()
    | other -> failwith $"Expected a parse error, got %A{other}"

[<Fact>]
let ``A block scrutinee indented no more than the match is an error`` () =
    match scrutineesOf "    match\n    xs \\length\n  | 0 -> 1\n  | _ -> 0" with
    | Result.Error _ -> ()
    | other -> failwith $"Expected a parse error, got %A{other}"

[<Fact>]
let ``Or-patterns work in pattern lambdas and with several scrutinees`` () =
    match parseModule "def f = { | 0, _ | _, 0 -> \"axis\" | _, _ -> \"plane\" }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (args, Kind (Match (_, arms))))))) ] ->
        args |> List.length |> should equal 2
        arms |> List.length |> should equal 3
        arms |> List.map (fst >> List.length) |> should equal [ 2; 2; 2 ]
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a name starting with an underscore is a name in a pattern, and a lone underscore a wildcard`` () =
    match parseModule "def x = match v\n  | [_first, _] -> 1\n  | _other -> 2" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Match (_, [ [ Kind (ListPattern ([ Kind (IdentifierPattern "_first"); Kind WildcardPattern ], None)) ], _
                                                                    [ Kind (IdentifierPattern "_other") ], _ ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
