/// The recursive-descent parser's lambdas, blocks and statements, at the places where reading them takes care: each is
/// read as the FParsec parser reads it, with the same spans (see `DescentCheck.checkExpressions`).
module Nanyx.Parser.Tests.DescentBlockTests

open Xunit
open Parser.Program
open Nanyx.Parser

/// How the new parser reads each def's value and each statement of `source`.
let private outcomesIn (source: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed -> DescentCheck.checkExpressions "t.nyx" parsed (DescentCheck.expressionRoots parsed)
    | Result.Error error -> failwith error.Message

/// Each outcome of `source` is the same tree.
let private allSame (source: string) =
    let outcomes = outcomesIn source
    Assert.NotEmpty(outcomes)
    Assert.Equal<DescentCheck.Outcome list>(List.replicate outcomes.Length DescentCheck.Same, outcomes)

[<Theory>]
[<InlineData("{ }")>]
[<InlineData("{ x -> x + 1 }")>]
[<InlineData("{ x, y -> x * y }")>]
[<InlineData("{ -> 1 }")>]
[<InlineData("{ () -> 1 }")>]
[<InlineData("{ (x: int) -> x }")>]
[<InlineData("{ x: int, g: (int -> int) -> g(x) }")>]
[<InlineData("{ (g: int -> int) -> g(1) }")>]
[<InlineData("{ mut cell -> cell := 1 }")>]
[<InlineData("{ (a, b) -> a + b }")>]
[<InlineData("{ Email(s), _ -> s }")>]
[<InlineData("{ 42 }")>]
[<InlineData("{ + }")>]
[<InlineData("{ * 2 }")>]
[<InlineData("{ - 1 }")>]
[<InlineData("{ % 2 == 0 }")>]
[<InlineData("{ .age > 18 }")>]
[<InlineData("{ .name \\trim }")>]
[<InlineData("{ x -> def y = x; y }")>]
[<InlineData("{ x -> log(x); x }")>]
[<InlineData("xs \\map { x -> x * 2 }")>]
[<InlineData("xs \\fold(0) { acc, x -> acc + x }")>]
[<InlineData("f { x -> x }")>]
[<InlineData("f(1) { x -> x }")>]
[<InlineData("f(1, y = 2) { x -> x }")>]
[<InlineData("f() { x -> x }")>]
let ``A lambda on one line is read as the FParsec parser reads it`` (value: string) =
    allSame $"def v = {value}\n"

[<Fact>]
let ``A lambda's statements on the lines after it are read, at any column until its brace`` () =
    allSame "def main = {\n  def a = 1\n  rec go = { n -> go(n) }\n  mut total = 0\n  total := total + a\n    \\f\n  export def b: int = 2\n  println(a); println(b)\n}\n"

[<Fact>]
let ``A def's value may be an indented block`` () =
    allSame "def v =\n  def a = 1\n  a + 1\n\ndef w = {\n  def x =\n    def y = 2\n    y\n  x\n}\n"

[<Fact>]
let ``A def's type and its = may start lines of their own`` () =
    allSame "def f = {\n  def g\n    : int -> int\n    = { x -> x }\n  g(1)\n}\n"

[<Fact>]
let ``Lambdas nest in arguments over several lines`` () =
    allSame "def v = items \\map { item ->\n  def price = item.price\n  price * 2\n} \\filter { > 0 }\n"

[<Theory>]
[<InlineData("{ x ->\n  | 1 -> 2\n  | _ -> 3\n}", "a lambda of match arms")>]
[<InlineData("{ context l -> l }", "a lambda that names its context's instance")>]
[<InlineData("{ (x: int): int -> x }", "a lambda's result type")>]
let ``A lambda the descent parser doesn't read yet is left to the FParsec parser`` (value: string, reason: string) =
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.GaveUp reason ], outcomesIn $"def v = {value}\n")
