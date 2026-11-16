module ParserTests.Features.ContextNames

open Xunit
open Parser.Program
open ParserTestHelpers

// `context l` before a lambda's parameters names the instance of a context the lambda is given (#1445).

/// The lambda `def f = <lambda>` parses to: its parameters' names, the names its contexts are given with the contexts
/// written, and its body's kind under them.
let private lambdaOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", None, Kind (Lambda (parameters, Kind (ContextNames (names, body)), _))))) ] ->
        parameters |> List.map fst,
        names |> List.map (fun (name, context, _) -> name, context |> Option.map (fun (context: TypeExpr) -> context.Kind)),
        body.Kind
    | other -> failwith $"Unexpected parse of '{source}': %A{other}"

[<Fact>]
let ``context names the instance of a context before the lambda's parameters`` () =
    match lambdaOf "def f = { context l -> use l in g() }" with
    | [], [ "l", None ], UseIn (Kind (IdentifierExpr "l"), _) -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``context names can say which context, and come before ordinary parameters`` () =
    match lambdaOf "def f = { context m: Memory, context l: Log, x, y -> x }" with
    | [ "x"; "y" ], [ "m", Some (TypeName "Memory"); "l", Some (TypeName "Log") ], IdentifierExpr "x" -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``A lambda with context names may have a body of several lines`` () =
    match lambdaOf (normalized "def f = { context l ->\n  use l\n  g()\n}") with
    | [], [ "l", None ], Block [ Kind (UseStatement (Kind (IdentifierExpr "l"))); Kind (ExprStatement _) ] -> ()
    | other -> failwith $"Unexpected lambda: %A{other}"

[<Fact>]
let ``The span of a context name is the name`` () =
    let source = "def f = { context log -> () }"
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (ContextNames ([ _, _, span ], _)), _))))) ] ->
        Assert.Equal("log", source.Substring(span.Start, span.End - span.Start))
    | other -> failwith $"Unexpected parse: %A{other}"
