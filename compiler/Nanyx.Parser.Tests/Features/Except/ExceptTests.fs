module ParserTests.Features.Except

open Xunit
open Parser.Program
open ParserTestHelpers

/// The value of `source`'s one def.
let private valueOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The patterns of each arm, shown, and its body's kind.
let private armsOf (arms: MatchArm list) =
    arms |> List.map (fun (patterns, body) -> patterns |> List.map _.Kind, body.Kind)

[<Fact>]
let ``except takes one arm on the line of its value`` () =
    match valueOf "def v = Int.parse(s) except #invalidInt -> #invalidVersionNumber" with
    | Kind (Except (Kind (FunctionCall ("Int.parse", _, _)), arms)) ->
        Assert.Equal<(PatternKind list * ExpressionKind) list>(
            [ [ TagPattern ("invalidInt", None) ], TagExpr ("invalidVersionNumber", None) ],
            armsOf arms)
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except takes the arms of a match on the lines after it`` () =
    let source =
        "def v = load(n) except\n" +
        "  | #error(#notFound) -> #some(0)\n" +
        "  | #error(#io(message)) -> #some(1)\n"
    match valueOf source with
    | Kind (Except (Kind (FunctionCall ("load", _, _)), arms)) ->
        match armsOf arms with
        | [ [ TagPattern ("error", Some (Kind (TagPattern ("notFound", None)))) ], _
            [ TagPattern ("error", Some (Kind (TagPattern ("io", Some (Kind (IdentifierPattern "message")))))) ], _ ] -> ()
        | other -> failwith $"Unexpected arms: %A{other}"
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``an except arm may have alternatives`` () =
    match valueOf "def v = x except #a | #b -> #c" with
    | Kind (Except (_, arms)) ->
        Assert.Equal<(PatternKind list * ExpressionKind) list>(
            [ [ TagPattern ("a", None) ], TagExpr ("c", None); [ TagPattern ("b", None) ], TagExpr ("c", None) ],
            armsOf arms)
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except binds looser than pipes and operators`` () =
    match valueOf "def v = n + 1 \\lookup(table) except #notFound -> #some(0)" with
    | Kind (Except (Kind (Pipe (Kind (BinaryOp ("+", _, _)), "lookup", _, _)), [ _ ])) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except may start the line after its value, as a pipe may`` () =
    let source =
        "def v = xs\n" +
        "  \\find(p)\n" +
        "  except #notFound -> #none\n"
    match valueOf source with
    | Kind (Except (Kind (Pipe (_, "find", _, _)), [ _ ])) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except works as a statement and inside a lambda`` () =
    let source =
        "def parseNumber = { s ->\n" +
        "  def n = Int.parse(s) except #invalidInt -> #some(0)\n" +
        "  n\n" +
        "}\n"
    match valueOf source with
    | Kind (Lambda (_, Kind (Block [ Kind (DefStatement (_, "n", _, Kind (Except _))); _ ]))) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except spans from its value to its last arm`` () =
    let source = "def v = f(x) except #a -> #b"
    match valueOf source with
    | Kind (Except _) as value -> Assert.Equal("f(x) except #a -> #b", source.Substring(value.Span.Start, value.Span.End - value.Span.Start))
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``except is not a name a workflow keyword can be applied to`` () =
    // `x except ...` inside a lambda is not `x` applied to the value `except ...`.
    match valueOf "def f = { x -> x except #a -> #b }" with
    | Kind (Lambda (_, Kind (Except (Kind (IdentifierExpr "x"), _)))) -> ()
    | other -> failwith $"Unexpected value: %A{other}"
