module ParserTests.Features.Samples

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers
open System.IO

[<Fact>]
let ``Parse sample feature files`` () =
    let unsupported = set [ "real-world.nyx" ]

    parseFeatureNyxFiles "Samples"
    |> Array.filter (fun (filePath, _) -> not (unsupported.Contains(Path.GetFileName(filePath))))
    |> Array.iter (fun (filePath, result) ->
        match result with
        | Result.Ok _ -> ()
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
    )

[<Fact>]
let ``Parse sample nyx file`` () =
    let input = """module Test

def message = "Hello world"
def x = 42
def pi = 3.14"""
    
    let result = parseModule input
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 4
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "Test"
        | _ -> failwith "Expected ModuleDecl"
    | Result.Error err ->
        failwith $"Parse failed: {err}"
