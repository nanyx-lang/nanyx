/// The recursive-descent parser's patterns, at the places where reading them takes care: each is read as the FParsec
/// parser reads it, with the same spans (see `DescentCheck.checkPatterns`).
module Nanyx.Parser.Tests.DescentPatternTests

open Xunit
open Parser.Program
open Nanyx.Parser

/// How the new parser reads each pattern of the arms of `match x` with an arm for each of `patterns`.
let private outcomesOf (patterns: string list) =
    let arms = patterns |> List.map (fun pattern -> $"  | {pattern} -> 1") |> String.concat "\n"
    let source = $"def f = {{ x ->\n  match x\n{arms}\n}}\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        let roots = DescentCheck.patternRoots parsed
        Assert.Equal(patterns.Length, roots.Length)
        DescentCheck.checkPatterns "t.nyx" parsed roots
    | Result.Error error -> failwith error.Message

[<Theory>]
[<InlineData("-1")>]
[<InlineData("+1_000")>]
[<InlineData("0x1F")>]
[<InlineData("2.5")>]
[<InlineData("-2.5e3")>]
[<InlineData("1.")>]
[<InlineData("1..10")>]
[<InlineData("-5..-1")>]
[<InlineData("\"a\\tb\"")>]
[<InlineData("'raw\\n'")>]
[<InlineData("true")>]
[<InlineData("_")>]
[<InlineData("_rest")>]
[<InlineData("else")>]
[<InlineData("()")>]
[<InlineData("(a, (b), _)")>]
[<InlineData("(name = n, age = _)")>]
[<InlineData("(name = n, ...)")>]
[<InlineData("Email(raw)")>]
[<InlineData("[]")>]
[<InlineData("[a, ...rest]")>]
[<InlineData("[a, ...]")>]
[<InlineData("[first, ..., last]")>]
[<InlineData("#none")>]
[<InlineData("#some()")>]
[<InlineData("#some(x)")>]
[<InlineData("#pair(a, b)")>]
[<InlineData("#point(x = 0, ...)")>]
[<InlineData("#error e")>]
[<InlineData("#error #some(1)")>]
[<InlineData("#Even")>]
[<InlineData("#Even(n)")>]
[<InlineData("> 5")>]
[<InlineData(">= -1")>]
[<InlineData("#some(v) & whole")>]
let ``A pattern is read as the FParsec parser reads it`` (pattern: string) =
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], outcomesOf [ pattern ])

[<Fact>]
let ``An and-pattern may go on after a line break`` () =
    let source = "def f = { x ->\n  match x\n    | #some(v)\n      & whole -> 1\n    | _ -> 0\n}\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        let roots = DescentCheck.patternRoots parsed
        Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same; DescentCheck.Same ], DescentCheck.checkPatterns "t.nyx" parsed roots)
    | Result.Error error -> failwith error.Message

[<Theory>]
[<InlineData("{ < 0 }")>]
[<InlineData("{ x -> x % 2 == 0 }")>]
[<InlineData("#DivisibleBy(3)")>]
[<InlineData("#DivisibleBy(3)(n)")>]
[<InlineData("#Between(1, 10)")>]
let ``A pattern that holds an expression is read as the FParsec parser reads it`` (pattern: string) =
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], outcomesOf [ pattern ])
