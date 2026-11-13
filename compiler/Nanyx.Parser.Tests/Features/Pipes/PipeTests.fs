module ParserTests.Features.Pipes

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse piping.nyx file`` () =
    let result = parseTestFile "piping.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> List.length |> should be (greaterThan 5)
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "Piping"
        | _ -> failwith "Expected ModuleDecl"

        let hasPipe =
            module' |> List.exists (fun item ->
                match item with
                | Kind (Def (ValueDef (_, _, _, Kind (Pipe _)))) -> true
                | _ -> false
            )
        hasPipe |> should equal true
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A pipe target may take named arguments`` () =
    match parseModule "def r = x \\f(a, y = z)" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Pipe (_, "f", _, [ args ]))))) ] ->
        match args.Kind with
        | RecordExpr [ PositionalField (Kind (IdentifierExpr "a")); NamedField ("y", Kind (IdentifierExpr "z")) ] -> ()
        | other -> failwith $"Expected record arguments, got: {other}"
    | other -> failwith $"Expected a single def of a pipe, got: {other}"

let private callArgumentsOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Pipe (value, _, _, args))))) ] -> pipeCallArguments value args |> List.map _.Kind
    | other -> failwith $"Expected a single def of a pipe, got: {other}"

[<Fact>]
let ``A piped value goes in front of several written arguments`` () =
    match callArgumentsOf "def r = x \\f(a, b) { y -> y }" with
    | [ IdentifierExpr "x"; IdentifierExpr "a"; IdentifierExpr "b"; Lambda _ ] -> ()
    | other -> failwith $"Expected x, a, b and the lambda, got: {other}"

[<Fact>]
let ``A piped value goes in front of named arguments, in the same record`` () =
    match callArgumentsOf "def r = x \\f(a, y = z) { w -> w }" with
    | [ RecordExpr [ PositionalField (Kind (IdentifierExpr "x"))
                     PositionalField (Kind (IdentifierExpr "a"))
                     PositionalField (Kind (Lambda _))
                     NamedField ("y", Kind (IdentifierExpr "z")) ] ] -> ()
    | other -> failwith $"Expected one record of x, a, the lambda and y, got: {other}"

[<Fact>]
let ``A tuple in brackets of its own is one argument of a pipe`` () =
    match callArgumentsOf "def r = x \\f((a, b)) { y -> y }" with
    | [ IdentifierExpr "x"; TupleExpr [ Kind (IdentifierExpr "a"); Kind (IdentifierExpr "b") ]; Lambda _ ] -> ()
    | other -> failwith $"Expected x, the tuple and the lambda, got: {other}"

/// The one expression statement of the lambda in `def f = { n -> <body> }`.
let private lambdaStatementOf (body: string) =
    match parseModule ("def f = { n ->\n" + body + "\n}") with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement expr) ]), _))))) ] -> expr.Kind
    | other -> failwith $"Expected a lambda of one expression statement, got: %A{other}"

[<Fact>]
let ``A pipe chain in a lambda body continues on lines indented more than its first`` () =
    match lambdaStatementOf "  n\n    \\add(2)\n    \\add(3)" with
    | Pipe (Kind (Pipe (Kind (IdentifierExpr "n"), "add", _, _)), "add", _, _) -> ()
    | other -> failwith $"Expected n \\add(2) \\add(3), got: %A{other}"

[<Fact>]
let ``A pipe chain may continue after a comment line`` () =
    match lambdaStatementOf "  n\n    \\add(2) -- two\n    -- and three\n    \\add(3)" with
    | Pipe (Kind (Pipe (Kind (IdentifierExpr "n"), "add", _, _)), "add", _, _) -> ()
    | other -> failwith $"Expected n \\add(2) \\add(3), got: %A{other}"

[<Fact>]
let ``A pipe chain in a match arm's block continues on more indented lines`` () =
    let body =
        "  match n\n" +
        "    | 0 ->\n" +
        "      n\n" +
        "        \\add(2)\n" +
        "    | _ -> n"
    match lambdaStatementOf body with
    | Match (_, [ _, Kind (Block [ Kind (ExprStatement (Kind (Pipe (Kind (IdentifierExpr "n"), "add", _, _)))) ]); _ ]) -> ()
    | other -> failwith $"Expected the first arm's block to be n \\add(2), got: %A{other}"

[<Fact>]
let ``A pipe at the indent of the statement's line does not continue it`` () =
    match parseModule "def f = { n ->\n  n\n  \\add(2)\n}" with
    | Result.Error err -> err |> should haveSubstring "Ln: 3"
    | other -> failwith $"Expected a parse error, got: %A{other}"

[<Fact>]
let ``except may start a more indented line after a pipe chain in a lambda body`` () =
    match lambdaStatementOf "  n\n    \\find\n    except #notFound -> #none" with
    | Except (Kind (Pipe (_, "find", _, [])), [ _ ]) -> ()
    | other -> failwith $"Expected n \\find except ..., got: %A{other}"

[<Fact>]
let ``\match matches on the value piped into it`` () =
    match parseModule "def r = s \\trim \\match\n  | \"\" -> 0\n  | _ -> 1" with
    | Result.Ok [ Kind (Def (ValueDef (_, "r", _, Kind (Match ([ Kind (Pipe (Kind (IdentifierExpr "s"), "trim", _, [])) ], arms))))) ] ->
        arms |> should haveLength 2
    | other -> failwith $"Expected a match on s \\trim, got: %A{other}"

[<Fact>]
let ``\match continues a pipe chain in a lambda body, and the chain continues after its arms`` () =
    let body =
        "  n\n" +
        "    \\match\n" +
        "      | 0 -> \"zero\"\n" +
        "      | _ -> \"other\"\n" +
        "    \\println"
    match lambdaStatementOf body with
    | Pipe (Kind (Match ([ Kind (IdentifierExpr "n") ], [ _; _ ])), "println", _, []) -> ()
    | other -> failwith $"Expected (n \\match ...) \\println, got: %A{other}"

[<Fact>]
let ``A name that starts with match is still a pipe target`` () =
    match parseModule "def r = x \\matches(y)" with
    | Result.Ok [ Kind (Def (ValueDef (_, "r", _, Kind (Pipe (_, "matches", _, [ _ ]))))) ] -> ()
    | other -> failwith $"Expected a pipe into matches, got: %A{other}"

// Pipe Operator Tests
[<Fact>]
let ``Parse simple pipe`` () =
    let result = parseModule "def result = x \\f"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Pipe (expr, funcName, _, args))))) ->
            name |> should equal "result"
            match expr.Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier in pipe left side"
            funcName |> should equal "f"
            args |> should haveLength 0
        | _ -> failwith "Expected Def with Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse pipe with arguments`` () =
    let result = parseModule "def result = x \\f(y, z)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Pipe (expr, funcName, _, args))))) ->
            name |> should equal "result"
            match expr.Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier"
            funcName |> should equal "f"
            // Each value in the brackets is an argument of its own, so that `\f((y, z))` can pass a tuple.
            match args |> List.map _.Kind with
            | [ IdentifierExpr y; IdentifierExpr z ] ->
                y |> should equal "y"
                z |> should equal "z"
            | _ -> failwith "Expected two identifiers as arguments"
        | _ -> failwith "Expected Def with Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse chained pipes`` () =
    let result = parseModule "def result = x \\f \\g \\h"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Pipe (Kind (Pipe (Kind (Pipe (expr, f, _, _)), g, _, _)), h, _, _))))) ->
            name |> should equal "result"
            match expr.Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier"
            f |> should equal "f"
            g |> should equal "g"
            h |> should equal "h"
        | _ -> failwith "Expected nested Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse pipe with literal`` () =
    let result = parseModule "def result = 5 \\double"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Pipe (expr, funcName, _, args))))) ->
            name |> should equal "result"
            match expr.Kind with
            | LiteralExpr (IntLit n) -> n |> should equal 5I
            | _ -> failwith "Expected int literal"
            funcName |> should equal "double"
            args |> should haveLength 0
        | _ -> failwith "Expected Def with Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse pipe with lambda argument`` () =
    let result = parseModule "def result = items \\map({ x -> x * 2 })"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Pipe (expr, funcName, _, args))))) ->
            name |> should equal "result"
            match expr.Kind with
            | IdentifierExpr id -> id |> should equal "items"
            | _ -> failwith "Expected identifier"
            funcName |> should equal "map"
            args |> should haveLength 1
            match args.[0].Kind with
            | Lambda (parameters, _, _) ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal "x"
            | _ -> failwith "Expected lambda argument"
        | _ -> failwith "Expected Def with Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
