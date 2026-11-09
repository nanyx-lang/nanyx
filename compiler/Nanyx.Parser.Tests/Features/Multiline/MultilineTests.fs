module ParserTests.Features.Multiline

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse multiline feature files`` () =
    parseFeatureNyxFiles "Multiline"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

// Complex Tests
[<Fact>]
let ``Parse multiline module`` () =
    let input = """module MyModule

def x = 1
def y = 2
def message = "Hello"
def flag = true"""
    
    let result = parseModule input
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 5  // 1 module decl + 4 defs
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse with blank lines`` () =
    let input = """module Test

def x = 1

def y = 2"""
    
    let result = parseModule input
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 3
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
