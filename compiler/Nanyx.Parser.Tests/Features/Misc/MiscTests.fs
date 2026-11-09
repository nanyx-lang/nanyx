module ParserTests.Features.Misc

open Xunit
open FsUnit.Xunit
open ParserTestHelpers
open Parser.Program

[<Fact>]
let ``try applied to a value is a workflow bind`` () =
    match parseModule "def f = proc {\n  def a = try g(1)\n  return a\n}\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (FunctionCall ("proc", _, [ Kind (Lambda ([], Kind (Block statements))) ]))))) ] ->
        match statements with
        | [ Kind (DefStatement (_, "a", None, Kind (WorkflowBindExpr ("try", Kind (FunctionCall ("g", _, _))))))
            Kind (ExprStatement (Kind (WorkflowReturnExpr (Kind (IdentifierExpr "a"))))) ] -> ()
        | other -> failwith $"Unexpected statements: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``try can be applied inside an expression, and takes in the rest of it`` () =
    match parseModule "def f = proc {\n  return R(a = try g(1) \\h, b = 2)\n}\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (FunctionCall ("proc", _, [ Kind (Lambda ([], body)) ]))))) ] ->
        match body.Kind with
        | Block [ Kind (ExprStatement (Kind (WorkflowReturnExpr (Kind (FunctionCall ("R", _, [ Kind (RecordExpr fields) ])))))) ] ->
            match fields with
            | [ NamedField ("a", Kind (WorkflowBindExpr ("try", Kind (Pipe (_, "h", _, []))))); NamedField ("b", _) ] -> ()
            | other -> failwith $"Unexpected fields: %A{other}"
        | other -> failwith $"Unexpected body: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a name that starts with try is not the keyword`` () =
    match parseModule "def f = { s -> tryParse(s) }\n" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (Lambda (_, Kind (FunctionCall ("tryParse", _, _))))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a keyword other than try applied to a value is reported where it is written`` () =
    let source =
        "def numbers = { n ->\n"
        + "  seq {\n"
        + "    def x = yield n\n"
        + "    x\n"
        + "  }\n"
        + "}\n"
    match parseModule source with
    | Result.Error err ->
        err |> should haveSubstring "Ln: 3 Col: 13"
        err |> should haveSubstring "`yield` is applied to a value without parentheses"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``a name followed by a keyword operator is not a workflow keyword`` () =
    match parseModule "def f = { a, b -> if a and b then a else b }\n" with
    | Result.Ok _ -> ()
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``outside a lambda two names in a row are not taken for a workflow keyword`` () =
    match parseModule "pattern string -> int = { s -> 1 }\n" with
    | Result.Error err -> err |> should not' (haveSubstring "workflow keyword")
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``Parse misc feature files`` () =
    parseFeatureNyxFiles "Misc"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

// Whitespace Tests
[<Fact>]
let ``Parse with leading whitespace`` () =
    let result = parseModule "   def x = 1"
    result |> isOk |> should equal true

[<Fact>]
let ``Parse with trailing whitespace`` () =
    let result = parseModule "def x = 1   "
    result |> isOk |> should equal true

[<Fact>]
let ``Parse with extra whitespace around equals`` () =
    let result = parseModule "def x    =    1"
    result |> isOk |> should equal true

[<Fact>]
let ``Parse empty input`` () =
    let result = parseModule ""
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should be Empty
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse only whitespace`` () =
    let result = parseModule "   \n  \n  "
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should be Empty
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

// Error Tests
[<Fact>]
let ``Error on missing equals`` () =
    let result = parseModule "def x 42"
    result |> isError |> should equal true

[<Fact>]
let ``Error on missing value`` () =
    let result = parseModule "def x ="
    result |> isError |> should equal true

[<Fact>]
let ``Error on missing identifier`` () =
    let result = parseModule "def = 42"
    result |> isError |> should equal true

[<Fact>]
let ``Error on unterminated string`` () =
    let result = parseModule "def x = \"unterminated"
    result |> isError |> should equal true
