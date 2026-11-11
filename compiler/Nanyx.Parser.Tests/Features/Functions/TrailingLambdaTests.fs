module ParserTests.Features.TrailingLambdas

open Xunit
open Parser.Program
open ParserTestHelpers

/// The arguments of the call in `def r = <call>`.
let private callArgumentsOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (FunctionCall (_, _, args))))) ] -> args |> List.map _.Kind
    | other -> failwith $"Expected a single def of a call, got: {other}"

[<Fact>]
let ``A trailing lambda is the only argument of a call without brackets`` () =
    match callArgumentsOf "def r = f { y -> y }" with
    | [ Lambda _ ] -> ()
    | other -> failwith $"Expected the lambda, got: {other}"

[<Fact>]
let ``A trailing lambda follows one argument in brackets`` () =
    match callArgumentsOf "def r = f(a) { y -> y }" with
    | [ IdentifierExpr "a"; Lambda _ ] -> ()
    | other -> failwith $"Expected a and the lambda, got: {other}"

[<Fact>]
let ``A trailing lambda is the last of several arguments, not a second one after their tuple`` () =
    match callArgumentsOf "def r = f(a, b) { y -> y }" with
    | [ IdentifierExpr "a"; IdentifierExpr "b"; Lambda _ ] -> ()
    | other -> failwith $"Expected a, b and the lambda, got: {other}"

[<Fact>]
let ``A tuple in brackets stays one argument before a trailing lambda`` () =
    match callArgumentsOf "def r = f((a, b)) { y -> y }" with
    | [ TupleExpr [ Kind (IdentifierExpr "a"); Kind (IdentifierExpr "b") ]; Lambda _ ] -> ()
    | other -> failwith $"Expected the tuple and the lambda, got: {other}"

[<Fact>]
let ``A trailing lambda is the last positional argument before named ones`` () =
    match callArgumentsOf "def r = f(a, y = z) { w -> w }" with
    | [ RecordExpr [ PositionalField (Kind (IdentifierExpr "a"))
                     PositionalField (Kind (Lambda _))
                     NamedField ("y", Kind (IdentifierExpr "z")) ] ] -> ()
    | other -> failwith $"Expected one record of a, the lambda and y, got: {other}"

[<Fact>]
let ``Several arguments in brackets without a trailing lambda are one tuple`` () =
    match callArgumentsOf "def r = f(a, b)" with
    | [ TupleExpr [ Kind (IdentifierExpr "a"); Kind (IdentifierExpr "b") ] ] -> ()
    | other -> failwith $"Expected one tuple of a and b, got: {other}"
