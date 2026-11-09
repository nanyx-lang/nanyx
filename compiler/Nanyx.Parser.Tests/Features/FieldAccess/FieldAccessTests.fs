module ParserTests.Features.FieldAccess

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``a member of a call's result is a member access, while C(T).f is a context access`` () =
    match parseModule "def a = openFile(path).contents\ndef b = ToString(a).toString(x)" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (MemberAccess (Kind (FunctionCall ("openFile", _, _)), "contents", _)))))
                  Kind (Def (ValueDef (_, "b", _, Kind (ContextMemberCall (Kind (TypeApply ("ToString", _)), "toString", _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse field access feature files`` () =
    parseFeatureNyxFiles "FieldAccess"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

// Member Access Tests
[<Fact>]
let ``Parse simple member access`` () =
    let result = parseModule "def x = point.x"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (MemberAccess(Kind (IdentifierExpr objName), fieldName, _))))) ->
            name |> should equal "x"
            objName |> should equal "point"
            fieldName |> should equal "x"
        | _ -> failwith "Expected Def with MemberAccess"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse nested member access`` () =
    let result = parseModule "def x = outer.inner.value"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (MemberAccess(Kind (MemberAccess(Kind (IdentifierExpr outer), inner, _)), value, _))))) ->
            name |> should equal "x"
            outer |> should equal "outer"
            inner |> should equal "inner"
            value |> should equal "value"
        | _ -> failwith "Expected Def with nested MemberAccess"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse member access in binary operation`` () =
    let result = parseModule "def sum = point.x + point.y"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (BinaryOp("+", Kind (MemberAccess(Kind (IdentifierExpr p1), f1, _)), Kind (MemberAccess(Kind (IdentifierExpr p2), f2, _))))))) ->
            name |> should equal "sum"
            p1 |> should equal "point"
            f1 |> should equal "x"
            p2 |> should equal "point"
            f2 |> should equal "y"
        | _ -> failwith "Expected Def with BinaryOp containing MemberAccess"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
