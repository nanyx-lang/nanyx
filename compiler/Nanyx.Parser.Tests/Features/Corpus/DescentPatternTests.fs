/// Patterns, at the places where reading them takes care: each is read with no mistake.
module Nanyx.Parser.Tests.DescentPatternTests

open Xunit
open Parser.Program
open ParserTestHelpers

/// Asserts that `match x` with an arm for each of `patterns` is read with no mistake.
let private readsArms (patterns: string list) =
    let arms = patterns |> List.map (fun pattern -> $"  | {pattern} -> 1") |> String.concat "\n"
    readsWhole $"def f = {{ x ->\n  match x\n{arms}\n}}\n"

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
let ``A pattern is read`` (pattern: string) =
    readsArms [ pattern ]

[<Fact>]
let ``An and-pattern may go on after a line break`` () =
    let source = "def f = { x ->\n  match x\n    | #some(v)\n      & whole -> 1\n    | _ -> 0\n}\n"
    readsWhole source

[<Theory>]
[<InlineData("{ < 0 }")>]
[<InlineData("{ x -> x % 2 == 0 }")>]
[<InlineData("#DivisibleBy(3)")>]
[<InlineData("#DivisibleBy(3)(n)")>]
[<InlineData("#Between(1, 10)")>]
let ``A pattern that holds an expression is read`` (pattern: string) =
    readsArms [ pattern ]
