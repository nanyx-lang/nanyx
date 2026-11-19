/// The recursive-descent parser's expressions, at the places where reading them takes care: each is read as the FParsec
/// parser reads it, with the same spans (see `DescentCheck.checkExpressions`).
module Nanyx.Parser.Tests.DescentExpressionTests

open Xunit
open Parser.Program
open Nanyx.Parser

/// How the new parser reads each def's value and each statement of `source`.
let private outcomesIn (source: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed -> DescentCheck.checkExpressions "t.nyx" parsed (DescentCheck.expressionRoots parsed)
    | Result.Error error -> failwith error.Message

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
let ``A def's value is read as the FParsec parser reads it`` (value: string) =
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], outcomesIn $"def v = {value}\n")

[<Fact>]
let ``A def's value may go on after an operator at the end of its line, and a pipe on any line after`` () =
    let source = "def v = a +\n  b *\n  c\n  \\f\n"
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], outcomesIn source)

[<Fact>]
let ``A statement stops at the end of its line, but for a pipe or an assignment`` () =
    let source = "def f = { x ->\n  g(x)\n    \\h\n  total := total + 1\n  a + b\n}\n"
    Assert.Equal<DescentCheck.Outcome list>(List.replicate 3 DescentCheck.Same, outcomesIn source |> List.skip 1)

[<Fact>]
let ``Fields written one per line need no commas`` () =
    let source = "def v = Point(\n  x = 1\n  y = 2\n)\n"
    Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], outcomesIn source)

[<Theory>]
[<InlineData("a < b < c", "two operators that can't be written side by side, which is a mistake")>]
let ``An expression the descent parser doesn't read is left to the FParsec parser`` (value: string, reason: string) =
    let source = $"def v = {value}\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        let outcomes = DescentCheck.checkExpressions "t.nyx" parsed (DescentCheck.expressionRoots parsed)
        Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.GaveUp reason ], outcomes)
    | Result.Error _ -> Assert.Equal("a < b < c", value)

[<Fact>]
let ``A type's field defaults and attributes are read as expressions`` () =
    let source = "type Options = (\n  retries: int = 3\n  @customKeywordType(#bind)\n  bind: () -> () = { () }\n  label: string = \"x {retries}\"\n)\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        let roots = DescentCheck.typeRoots parsed
        Assert.Equal<DescentCheck.Outcome list>([ DescentCheck.Same ], DescentCheck.checkTypes "t.nyx" parsed roots)
    | Result.Error error -> failwith error.Message
