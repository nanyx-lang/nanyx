module ParserTests.Features.AndPatterns

open Xunit
open Parser.Program
open ParserTestHelpers

/// The patterns of each arm of `source`'s one def, a lambda that matches.
let private armsOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Match (_, arms)), _))))) ] -> arms |> List.map fst
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``an and-pattern joins two patterns or more with &`` () =
    match armsOf "def f = { | { < 0 } & n -> 1 | n & 5 -> 2 | a & (b, c) & _ -> 3 }" with
    | [ [ Kind (AndPattern [ Kind (GuardPattern _); Kind (IdentifierPattern "n") ]) ]
        [ Kind (AndPattern [ Kind (IdentifierPattern "n"); Kind (LiteralPattern (IntLit 5L)) ]) ]
        [ Kind (AndPattern [ Kind (IdentifierPattern "a"); Kind (TuplePattern _); Kind WildcardPattern ]) ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``an and-pattern nests inside other patterns`` () =
    match armsOf "def f = { | #some(n & { > 1 }) -> 1 | (a & 1, b) -> 2 | [x & y, ...] -> 3 }" with
    | [ [ Kind (TagPattern ("some", Some (Kind (AndPattern [ Kind (IdentifierPattern "n"); Kind (GuardPattern _) ])))) ]
        [ Kind (TuplePattern [ Kind (AndPattern [ Kind (IdentifierPattern "a"); Kind (LiteralPattern (IntLit 1L)) ]); _ ]) ]
        [ Kind (ListPattern ([ Kind (AndPattern [ Kind (IdentifierPattern "x"); Kind (IdentifierPattern "y") ]) ], Some _)) ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``an and-pattern binds tighter than an or-pattern and looser than a tag's payload`` () =
    // `#error data & d` is `(#error data) & d`, as a call binds tighter than an operator.
    match armsOf "def f = { | #error data & d | n & 0 -> 1 | _ -> 2 }" with
    | [ [ Kind (AndPattern [ Kind (TagPattern ("error", Some (Kind (IdentifierPattern "data")))); Kind (IdentifierPattern "d") ]) ]
        [ Kind (AndPattern [ Kind (IdentifierPattern "n"); Kind (LiteralPattern (IntLit 0L)) ]) ]
        [ Kind WildcardPattern ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``&& after a pattern is still the operator`` () =
    // A lambda whose body starts with a name and `&&` is not a pattern parameter.
    match parseModule "def f = { a -> { a && b } }" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Lambda (_, Kind (BinaryOp (_, _, Kind (IdentifierExpr "a"), Kind (IdentifierExpr "b"))), _)), _))))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``an and-pattern spans its parts`` () =
    let source = "def f = { | { < 0 } & n -> n }"
    match armsOf source with
    | [ [ pattern ] ] ->
        Assert.Equal(source.IndexOf "{ < 0 }", pattern.Span.Start)
        Assert.Equal(source.IndexOf " ->", pattern.Span.End)
    | other -> failwith $"Unexpected arms: %A{other}"
