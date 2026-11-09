module ParserTests.Features.Lambdas

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse lambdas.nyx file`` () =
    let result = parseTestFile "lambdas.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 7  // 1 module + 6 lambda defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "Lambdas"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        for def in defs do
            match def with
            | Kind (Def (ValueDef (_, _, _, Kind (Lambda _)))) -> ()
            | _ -> failwith "Expected all definitions to contain lambdas"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse shorthand-lambdas.nyx file`` () =
    let result = parseTestFile "shorthand-lambdas.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 15  // 1 module + 14 shorthand lambda defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "ShorthandLambdas"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        for def in defs do
            match def with
            | Kind (Def (ValueDef (_, _, _, Kind (Lambda _)))) -> ()
            | Kind (Def (ValueDef (_, _, _, Kind (FunctionCall (_, _, args))))) ->
                let rec hasLambda (arg: Expression) =
                    match arg.Kind with
                    | Lambda _ -> true
                    | TupleExpr exprs -> exprs |> List.exists hasLambda
                    | _ -> false
                let foundLambda = args |> List.exists hasLambda
                foundLambda |> should equal true
            | _ -> failwith "Expected lambda or function call with lambda"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``an empty block is a lambda returning unit`` () =
    match parseModule "def main = {\n}\ndef noop = { }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind UnitExpr))))); Kind (Def (ValueDef (_, "noop", None, Kind (Lambda ([], Kind UnitExpr))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

// Lambda Expression Tests
[<Fact>]
let ``Parse lambda with no parameters`` () =
    let result = parseModule "def f = { 42 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "f"
            parameters |> should haveLength 0
            match body.Kind with
            | LiteralExpr (IntLit v) -> v |> should equal 42
            | _ -> failwith "Expected int literal in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with one parameter`` () =
    let result = parseModule "def f = { x -> x }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "f"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | IdentifierExpr id -> id |> should equal "x"
            | _ -> failwith "Expected identifier in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with multiple parameters`` () =
    let result = parseModule "def add = { x, y -> 42 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "add"
            parameters |> should haveLength 2
            parameters.[0] |> fst |> should equal "x"
            parameters.[1] |> fst |> should equal "y"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda with function call in body`` () =
    let result = parseModule "def f = { x -> println(x) }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "f"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | FunctionCall (funcName, _, args) ->
                funcName |> should equal "println"
                args |> should haveLength 1
            | _ -> failwith "Expected function call in lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse lambda as function argument`` () =
    let result = parseModule "def x = map({ x -> x }, items)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "x"
            funcName |> should equal "map"
            // With comma operator, f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (Lambda (parameters, _)); Kind (IdentifierExpr "items")] ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal "x"
            | _ -> failwith "Expected tuple with lambda and identifier"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

// Shorthand Lambda Tests
[<Fact>]
let ``Parse shorthand lambda with binary operator`` () =
    let result = parseModule "def multiply = { * }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "multiply"
            parameters |> should haveLength 2
            parameters.[0] |> fst |> should equal "x"
            parameters.[1] |> fst |> should equal "y"
            match body.Kind with
            | BinaryOp (op, Kind (IdentifierExpr left), Kind (IdentifierExpr right)) ->
                op |> should equal "*"
                left |> should equal "x"
                right |> should equal "y"
            | _ -> failwith "Expected binary operation in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with unary operation`` () =
    let result = parseModule "def double = { * 2 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "double"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | BinaryOp (op, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                op |> should equal "*"
                left |> should equal "x"
                right |> should equal 2
            | _ -> failwith "Expected binary operation with literal in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with property access`` () =
    let result = parseModule "def getName = { .name }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "getName"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | MemberAccess (Kind (IdentifierExpr obj), field, _) ->
                obj |> should equal "x"
                field |> should equal "name"
            | _ -> failwith "Expected member access in shorthand property lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda with addition`` () =
    let result = parseModule "def addFive = { + 5 }"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Lambda (parameters, body))))) ->
            name |> should equal "addFive"
            parameters |> should haveLength 1
            parameters.[0] |> fst |> should equal "x"
            match body.Kind with
            | BinaryOp (op, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                op |> should equal "+"
                left |> should equal "x"
                right |> should equal 5
            | _ -> failwith "Expected addition operation in shorthand lambda body"
        | _ -> failwith "Expected Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse shorthand lambda as function argument`` () =
    let result = parseModule "def doubled = map({ * 2 }, numbers)"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (FunctionCall (funcName, _, args))))) ->
            name |> should equal "doubled"
            funcName |> should equal "map"
            // With comma operator, f(x, y) creates a single tuple argument
            args |> should haveLength 1
            match args.[0].Kind with
            | TupleExpr [Kind (Lambda (parameters, body)); Kind (IdentifierExpr "numbers")] ->
                parameters |> should haveLength 1
                parameters.[0] |> fst |> should equal "x"
                match body.Kind with
                | BinaryOp (op, Kind (IdentifierExpr left), Kind (LiteralExpr (IntLit right))) ->
                    op |> should equal "*"
                    left |> should equal "x"
                    right |> should equal 2
                | _ -> failwith "Expected multiplication in lambda"
            | _ -> failwith "Expected shorthand lambda as first argument"
        | _ -> failwith "Expected Def with FunctionCall"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
