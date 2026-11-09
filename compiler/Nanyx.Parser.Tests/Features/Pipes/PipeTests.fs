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
            // With comma operator, \f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (IdentifierExpr y); Kind (IdentifierExpr z)] ->
                y |> should equal "y"
                z |> should equal "z"
            | _ -> failwith "Expected tuple of identifiers as arguments"
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
            | LiteralExpr (IntLit n) -> n |> should equal 5
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
            | Lambda (parameters, _) ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal "x"
            | _ -> failwith "Expected lambda argument"
        | _ -> failwith "Expected Def with Pipe"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
