/// Operators: the formatter brackets them as the parser groups them, both reading the one list `binaryOperators`.
module Nanyx.Formatter.Tests.OperatorTests

open Xunit
open Nanyx.Formatter
open Parser.Program
open Nanyx.Formatter.Tests.TestHelpers

let private valueOf (source: string) =
    match parseModuleFile "t.nyx" source with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, value))) ] -> value
    | other -> failwith $"Expected one def of x in {source}, got %A{other}"

/// Each pair of operators, grouped either way: `(a op b) op' c` and `a op (b op' c)`.
let pairs: obj array seq =
    seq {
        for first in binaryOperators do
            for second in binaryOperators do
                yield [| box $"def x = (a {first.Text} b) {second.Text} c" |]
                yield [| box $"def x = a {first.Text} (b {second.Text} c)" |]
    }

[<Theory>]
[<MemberData(nameof pairs)>]
let ``Formatting keeps how operators group`` (source: string) =
    let formatted = format source
    Assert.Equal(valueOf source, valueOf formatted)
    // And the formatted source is formatted.
    Assert.Equal(formatted, format formatted)

[<Theory>]
[<InlineData("def x = a  ??   b", "def x = a ?? b\n")>]
[<InlineData("def x = a ?? (b ?? c)", "def x = a ?? b ?? c\n")>]
[<InlineData("def x = (a ?? b) ?? c", "def x = (a ?? b) ?? c\n")>]
[<InlineData("def x = (a ?? b) == c", "def x = a ?? b == c\n")>]
[<InlineData("def x = a ?? (b == c)", "def x = a ?? (b == c)\n")>]
let ``?? is spaced, and bracketed only where it does not group as written`` (source: string) (expected: string) =
    Assert.Equal(expected, format source)
