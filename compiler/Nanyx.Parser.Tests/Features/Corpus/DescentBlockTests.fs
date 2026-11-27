/// Lambdas, blocks and statements, at the places where reading them takes care: each is read with no mistake.
module Nanyx.Parser.Tests.DescentBlockTests

open Xunit
open Parser.Program
open ParserTestHelpers

let private allSame (source: string) = readsWhole source

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
let ``A lambda on one line is read`` (value: string) =
    allSame $"def v = {value}\n"

[<Fact>]
let ``A lambda's statements on the lines after it are read, at any column until its brace`` () =
    allSame "def main = {\n  def a = 1\n  rec go = { n -> go(n) }\n  mut total = 0\n  total := total + a\n    \\f\n  def b: int = 2\n  println(a); println(b)\n}\n"

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
[<InlineData("{ context l -> l }")>]
[<InlineData("{ context m: Memory, context l: Log, x -> x }")>]
[<InlineData("{ context l, (a, b) -> a }")>]
[<InlineData("{ (x: int): int -> x }")>]
[<InlineData("{ x: int, y: int: int -> x + y }")>]
let ``A lambda that names its context's instance or writes its result type is read`` (value: string) =
    allSame $"def v = {value}\n"

[<Fact>]
let ``A destructuring def is read as the defs it desugars into`` () =
    allSame "def f = { p ->\n  def (a, b) = p\n  def x, y = p\n  def Money(pence) = m\n  def [first, ...rest] = xs\n  a\n}\n"

[<Fact>]
let ``An attribute statement is read before the def it applies to`` () =
    allSame "def f = {\n  @allow(default/unusedBindings, default/unusedResults)\n  def x = 1\n  @test()\n  def y = 2\n  @entry\n  x\n}\n"

[<Fact>]
let ``A table literal is the list of a record for each row`` () =
    allSame "def people = [| name     | age |\n                | \"Alex\"   | 22  |\n                | \"Taylor\" | 30  |]\n"

[<Theory>]
[<InlineData("{\n  | #a -> 1\n  | _ -> 2\n}")>]
[<InlineData("{ | 0 -> 1 | n -> n }")>]
[<InlineData("{ m, f ->\n  | #some(v), _ -> f(v)\n  | other, _ -> other\n}")>]
[<InlineData("{ a, b -> | 0, _ -> b | _, _ -> a }")>]
let ``A lambda of match arms is read`` (value: string) =
    allSame $"def v = {value}\n"
