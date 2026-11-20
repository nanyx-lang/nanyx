/// Expressions, at the places where reading them takes care: each is read with no mistake.
module Nanyx.Parser.Tests.DescentExpressionTests

open Xunit
open Parser.Program
open ParserTestHelpers

[<Theory>]
[<InlineData("1 + 2 * 3 ** 2 ** 2")>]
[<InlineData("a - b - c")>]
[<InlineData("xs ++ ys ++ zs")>]
[<InlineData("a ?? b ?? 0")>]
[<InlineData("not a and b or c == d")>]
[<InlineData("-x * y")>]
[<InlineData("-1 + +2")>]
[<InlineData("f >> g >> h")>]
[<InlineData("1..10")>]
[<InlineData("n - 1..n + 1")>]
[<InlineData("(1..)")>]
[<InlineData("[1.., 0..]")>]
[<InlineData("(1 + 2) * 3")>]
[<InlineData("(a, (b), c)")>]
[<InlineData("(x = 1, y = 2)")>]
[<InlineData("(...a, z = 3)")>]
[<InlineData("[1, ...xs, 2]")>]
[<InlineData("[\"a\" => 1, \"b\" => 2]")>]
[<InlineData("Array[1, 2]")>]
[<InlineData("Map[\"a\" => 1]")>]
[<InlineData("\"total: {a + b} of {n}\\n\"")>]
[<InlineData("'raw {x}'")>]
[<InlineData("f()")>]
[<InlineData("f(1, 2)(3)")>]
[<InlineData("m.f(x = 1, 2)")>]
[<InlineData("`adds points`(a, b)")>]
[<InlineData("#some(1)")>]
[<InlineData("#pair(a, b)")>]
[<InlineData("#some()")>]
[<InlineData("#none")>]
[<InlineData("p.x.y")>]
[<InlineData("t.0.name")>]
[<InlineData("(a + b).x")>]
[<InlineData("p with x = 1, y = 2")>]
[<InlineData("p with (x = 1)")>]
[<InlineData("Show(int).show(1)")>]
[<InlineData("Show(int).show")>]
[<InlineData("xs \\map(f) \\filter(g, h) \\sum")>]
[<InlineData("-x \\abs")>]
[<InlineData("pattern(with, in)")>]
[<InlineData("???")>]
[<InlineData("1, 2")>]
let ``A def's value is read`` (value: string) =
    readsWhole $"def v = {value}\n"

[<Fact>]
let ``A def's value may go on after an operator at the end of its line, and a pipe on any line after`` () =
    let source = "def v = a +\n  b *\n  c\n  \\f\n"
    readsWhole source

[<Fact>]
let ``A statement stops at the end of its line, but for a pipe or an assignment`` () =
    let source = "def f = { x ->\n  g(x)\n    \\h\n  total := total + 1\n  a + b\n}\n"
    readsWhole source

[<Fact>]
let ``Fields written one per line need no commas`` () =
    let source = "def v = Point(\n  x = 1\n  y = 2\n)\n"
    readsWhole source

[<Fact>]
let ``Two operators that can't be written side by side are a mistake`` () =
    match parseModuleWithTrivia "t.nyx" "def v = a < b < c\n" with
    | Result.Error error -> Assert.Equal((1, 15), (error.Span.StartLine + 1, error.Span.StartColumn + 1))
    | Result.Ok parsed -> Assert.Fail $"%A{parsed.Items}"

[<Fact>]
let ``A type's field defaults and attributes are read as expressions`` () =
    let source = "type Options = (\n  retries: int = 3\n  @customKeywordType(#bind)\n  bind: () -> () = { () }\n  label: string = \"x {retries}\"\n)\n"
    readsWhole source
