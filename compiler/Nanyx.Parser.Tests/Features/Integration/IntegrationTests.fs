module ParserTests.Features.Integration

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse comprehensive.nyx file`` () =
    let result = parseTestFile "comprehensive.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> List.length |> should be (greaterThan 5)
        match module'.[0] with
        | Kind (ModuleDecl(_, name)) -> name |> should equal "Comprehensive"
        | _ -> failwith "Expected ModuleDecl"

        let hasLiteral =
            module' |> List.exists (fun item ->
                match item with
                | Kind (Def (ValueDef (_, _, _, Kind (LiteralExpr _)))) -> true
                | _ -> false
            )

        let hasFunctionCall =
            module' |> List.exists (fun item ->
                match item with
                | Kind (Def (ValueDef (_, _, _, Kind (FunctionCall _)))) -> true
                | _ -> false
            )

        let hasLambda =
            module' |> List.exists (fun item ->
                match item with
                | Kind (Def (ValueDef (_, _, _, Kind (Lambda _)))) -> true
                | _ -> false
            )

        hasLiteral |> should equal true
        hasFunctionCall |> should equal true
        hasLambda |> should equal true
    | Result.Error err -> failwith $"Parse failed: {err}"
