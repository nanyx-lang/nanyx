module ParserTests.Features.Bindings

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse binding feature files`` () =
    parseFeatureNyxFiles "Bindings"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

// Module Tests
[<Fact>]
let ``Parse module declaration`` () =
    let result = parseModule "module Test"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (ModuleDecl name) ->
            name |> should equal "Test"
        | _ -> failwith "Expected ModuleDecl"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse dotted module declaration`` () =
    let input = """
module collections.list

export def one = 1
"""
    match parseModule (input.TrimStart()) with
    | Result.Ok module' ->
        module' |> should haveLength 2
        match module'.[0] with
        | Kind (ModuleDecl name) -> Assert.Equal("collections.list", name)
        | other -> failwith $"Expected ModuleDecl, got: {other}"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse module with definition`` () =
    let input = "module Test\ndef message = \"Hello\""
    let result = parseModule input
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 2
        match module'.[0] with
        | Kind (ModuleDecl _) -> ()
        | _ -> failwith "Expected ModuleDecl"
        match module'.[1] with
        | Kind (Def _) -> ()
        | _ -> failwith "Expected Def"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse ws-sensitive mid-file module block`` () =
    let input =
        "module Root\n" +
        "module Inner =\n" +
        "  type Person = (firstName: string)\n" +
        "  def Person.toString = { p -> \"person\" }\n" +
        "  def value = 1"
    let result = parseModule input

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 5
        match module'.[0] with
        | Kind (ModuleDecl "Root") -> ()
        | _ -> failwith "Expected ModuleDecl Root"
        match module'.[1] with
        | Kind (ModuleDecl "Root.Inner") -> ()
        | _ -> failwith "Expected ModuleDecl Root.Inner"
        match module'.[2] with
        | Kind (Def (TypeDef(name, _, _, _))) -> name |> should equal "Root.Inner.Person"
        | _ -> failwith "Expected qualified type def"
        match module'.[3] with
        | Kind (Def (ValueDef(_, name, _, _))) -> name |> should equal "Root.Inner.Person.toString"
        | _ -> failwith "Expected qualified attached def"
        match module'.[4] with
        | Kind (Def (ValueDef(_, name, _, _))) -> name |> should equal "Root.Inner.value"
        | _ -> failwith "Expected qualified value def"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse multiple definitions`` () =
    let input = "def x = 1\ndef y = 2\ndef z = 3"
    let result = parseModule input
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 3
        for item in module' do
            match item with
            | Kind (Def _) -> ()
            | _ -> failwith "Expected all Def"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse definition with newline before equals`` () =
    let input = "def x\n  = 1"
    let result = parseModule input

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (IntLit value))))) ->
            name |> should equal "x"
            value |> should equal 1L
        | _ -> failwith "Expected Def with IntLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse typed definition with newline before equals`` () =
    let input = "def f: string -> ()\n  = { s -> dbg(s) }"
    let result = parseModule input

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, Some _, Kind (Lambda _)))) ->
            name |> should equal "f"
        | _ -> failwith "Expected typed Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse context-typed definition with newline before equals`` () =
    let input = "def f: [Print] string -> ()\n  = { s -> dbg(s) }"
    let result = parseModule input

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, Some _, Kind (Lambda _)))) ->
            name |> should equal "f"
        | _ -> failwith "Expected context-typed Def with Lambda"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

// Identifier Tests
[<Fact>]
let ``Parse identifier expression`` () =
    let result = parseModule "def x = someValue"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (IdentifierExpr value)))) ->
            name |> should equal "x"
            value |> should equal "someValue"
        | _ -> failwith "Expected Def with IdentifierExpr"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Identifier can start with underscore`` () =
    let result = parseModule "def _private = 42"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, _))) ->
            name |> should equal "_private"
        | _ -> failwith "Expected Def"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Identifier can contain digits`` () =
    let result = parseModule "def value123 = 999"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, _))) ->
            name |> should equal "value123"
        | _ -> failwith "Expected Def"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
