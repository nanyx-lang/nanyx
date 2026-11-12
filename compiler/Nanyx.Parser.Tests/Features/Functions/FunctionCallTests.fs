module ParserTests.Features.Functions

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse function-calls.nyx file`` () =
    let result = parseTestFile "function-calls.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 7  // 1 module + 6 function call defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "FunctionCalls"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        for def in defs do
            match def with
            | Kind (Def (ValueDef (_, _, _, Kind (FunctionCall _)))) -> ()
            | _ -> failwith "Expected all definitions to contain function calls"
    | Result.Error err -> failwith $"Parse failed: {err}"

// Function Call Tests
[<Fact>]
let ``Parse function call with no arguments`` () =
    let result = parseModule "def x = foo()"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "x"
            funcName |> should equal "foo"
            args |> should haveLength 0
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse function call with one argument`` () =
    let result = parseModule "def x = println(\"Hello\")"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "x"
            funcName |> should equal "println"
            args |> should haveLength 1
            match args.[0].Kind with
            | LiteralExpr (StringLit value) -> value |> should equal "Hello"
            | _ -> failwith "Expected string literal argument"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse function call with multiple arguments`` () =
    let result = parseModule "def result = add(1, 2)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "result"
            funcName |> should equal "add"
            // With comma operator, f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (LiteralExpr (IntLit v1)); Kind (LiteralExpr (IntLit v2))] -> 
                v1 |> should equal 1I
                v2 |> should equal 2I
            | _ -> failwith "Expected tuple of int literal arguments"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse function call with identifier argument`` () =
    let result = parseModule "def y = foo(x)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "y"
            funcName |> should equal "foo"
            args |> should haveLength 1
            match args.[0].Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier argument"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse nested function calls`` () =
    let result = parseModule "def x = outer(inner(42))"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "x"
            funcName |> should equal "outer"
            args |> should haveLength 1
            match args.[0].Kind with
            | FunctionCall (innerName, _, innerArgs) ->
                innerName |> should equal "inner"
                innerArgs |> should haveLength 1
                match innerArgs.[0].Kind with
                | LiteralExpr (IntLit v) -> v |> should equal 42I
                | _ -> failwith "Expected int literal in inner call"
            | _ -> failwith "Expected nested function call"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
