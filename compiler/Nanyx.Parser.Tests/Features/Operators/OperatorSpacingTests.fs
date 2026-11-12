/// Operators are written with a space on each side (#696): `x + y`, not `x+y`. Prefix `-` and `not`, ranges and the
/// shorthand lambdas that start with an operator are not binary operators written between operands.
module ParserTests.Features.OperatorSpacing

open Xunit
open Parser.Program
open ParserTestHelpers

[<Theory>]
[<InlineData("def x = a+b", "+", "+b")>]
[<InlineData("def x = a +b", "+", "+b")>]
[<InlineData("def x = a+ b", "+", "+ b")>]
[<InlineData("def x = a -b", "-", "-b")>]
[<InlineData("def x = a==b", "==", "==b")>]
[<InlineData("def x = a ** 2*b", "*", "*b")>]
[<InlineData("def x = a??b", "??", "??b")>]
[<InlineData("def x = p and(q)", "and", "and(q)")>]
[<InlineData("def f = { -x }", "-", "-x }")>]
[<InlineData("def s = \"{a+b}\"", "+", "+b}\"")>]
let ``An operator without a space on each side is an error at the operator`` (source: string) (op: string) (at: string) =
    let reported, text = parseErrorOf source
    Assert.Contains($"Operators are written with a space on each side: `x {op} y`, not `x{op}y`", reported)
    Assert.Equal(at, text)

[<Theory>]
[<InlineData("def x = a + b")>]
[<InlineData("def x = a +\n  b")>]
[<InlineData("def x = -a * -b")>]
[<InlineData("def x = not a")>]
[<InlineData("def x = 1..10")>]
[<InlineData("def x = f(-1)")>]
[<InlineData("def f = { + }")>]
[<InlineData("def f = { + 1 }")>]
[<InlineData("def f = { % 2 == 0 }")>]
[<InlineData("def f = { .length > 5 }")>]
[<InlineData("def s = \"{a + b}\"")>]
let ``Operators with a space on each side, and prefix operators against their operand, parse`` (source: string) =
    match parseModule source with
    | Result.Ok _ -> ()
    | Result.Error err -> failwith $"Parse failed: {err}"
