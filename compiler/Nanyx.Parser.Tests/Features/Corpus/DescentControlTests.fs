/// The recursive-descent parser's expressions that start with a keyword, at the places where reading them takes care:
/// each is read as the FParsec parser reads it, with the same spans (see `DescentCheck.checkExpressions`).
module Nanyx.Parser.Tests.DescentControlTests

open Xunit
open Parser.Program
open Nanyx.Parser

/// Each def's value and statement of `source` reads to the FParsec parser's tree.
let private allSame (source: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        let outcomes = DescentCheck.checkExpressions "t.nyx" parsed (DescentCheck.expressionRoots parsed)
        Assert.NotEmpty(outcomes)
        Assert.Equal<DescentCheck.Outcome list>(List.replicate outcomes.Length DescentCheck.Same, outcomes)
    | Result.Error error -> failwith error.Message

/// `body`, indented, as the statements of a lambda.
let private inLambda (body: string) =
    let indented = body.Split('\n') |> Array.map (fun line -> if line = "" then line else "  " + line) |> String.concat "\n"
    $"def f = {{ x ->\n{indented}\n}}\n"

[<Theory>]
[<InlineData("match x\n  | 0 -> \"zero\"\n  | n -> \"{n}\"")>]
[<InlineData("match x\n| 0 -> 1\n| _ -> 2")>]
[<InlineData("match x | 0 -> 1 | _ -> 2")>]
[<InlineData("match a, b\n  | 0, _ | _, 0 -> 0\n  | _, _ -> 1")>]
[<InlineData("match x\n  | #some(v) ->\n    def y = v\n    y\n  | #none -> 0")>]
[<InlineData("match x\n  | [a] ->\n    match a\n      | 0 -> 1\n      | _ -> 2\n  | _ -> 3")>]
[<InlineData("match\n    def t = x\n    t\n  | 0 -> 1\n  | _ -> 2")>]
[<InlineData("match x\n  | 1\n  | 2 -> \"small\"\n  | _ -> \"big\"")>]
[<InlineData("x \\match\n  | 0 -> 1\n  | _ -> 2")>]
[<InlineData("xs \\find(p)\n  except #notFound -> #none")>]
[<InlineData("parse(s) except\n  | #error(_) -> 0")>]
let ``A match is read as the FParsec parser reads it`` (body: string) =
    allSame (inLambda body)

[<Theory>]
[<InlineData("if a then b else c")>]
[<InlineData("if a then b")>]
[<InlineData("if a\n  then b\n  else c")>]
[<InlineData("if a then\n  def y = 1\n  y\nelse\n  2")>]
[<InlineData("if x is #some(v) then v else 0")>]
[<InlineData("if a then 1 else if b then 2 else 3")>]
[<InlineData("if\n  | a -> 1\n  | x is #some(v) -> v\n  | else -> 3")>]
[<InlineData("if | a -> 1 | b -> 2")>]
[<InlineData("if inst == #match\n  then 1\n  else 2")>]
let ``An if is read as the FParsec parser reads it`` (body: string) =
    allSame (inLambda body)

[<Theory>]
[<InlineData("for xs | x -> println(x)")>]
[<InlineData("for xs -> tick()")>]
[<InlineData("for xs, 0.. | x, i -> println(x)")>]
[<InlineData("for 1..3 | { < 2 } -> a() | _ -> b()")>]
[<InlineData("while n > 0 ->\n  n := n - 1")>]
[<InlineData("do\n  def a = 1\n  a")>]
[<InlineData("use Log(log = { m -> println(m) }) in greet()")>]
[<InlineData("use addInt in\n  def a = 1\n  a + 1")>]
[<InlineData("use Show(int)(show = { n -> \"{n}\" })\nshow(1)")>]
[<InlineData("use Sized(Bag, elem = int)(size = { b -> 0 })\nsize(b)")>]
[<InlineData("return x + 1")>]
[<InlineData("def y = try f(x)\nyield y")>]
[<InlineData("def y = try! f(x)\ny")>]
[<InlineData("update(mut cell, 1)")>]
[<InlineData("sizeOf(type Point)")>]
let ``A loop, a use, a workflow keyword and the rest are read as the FParsec parser reads them`` (body: string) =
    allSame (inLambda body)
