module ParserTests.Features.DoBlocks

open Xunit
open Parser.Program
open ParserTestHelpers

let private parse (source: string) = parseModuleFile "t.nyx" source

let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

[<Fact>]
let ``A do block is a block statement in the block around it`` () =
    let source = "def f = { ->\n  do\n    def x = 1\n    x\n  2\n}\n"
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ Kind (ExprStatement (Kind (Block inner) as block)); Kind (ExprStatement (Kind (LiteralExpr (IntLit64 2L)))) ]), _))))) ] ->
        Assert.Equal("def x = 1\n    x", textOf source block.Span)
        Assert.Equal(2, inner.Length)
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A do block can be a def's value`` () =
    let source = "def x =\n  do\n    def y = 1\n    y\n"
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (Block [ Kind (ExprStatement (Kind (Block [ _; _ ]))) ])))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A do without a block fails at the do`` () =
    let source = "def f = { ->\n  do 1\n}\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Error error ->
        Assert.Contains("`do` starts a block", error.Message)
        Assert.Equal(source.IndexOf "do", error.Span.Start)
    | other -> Assert.Fail $"Expected a parse error, got %A{other}"

[<Fact>]
let ``A name that starts with do is still a name`` () =
    match parse "def done = 1\ndef x = done\n" with
    | Result.Ok [ _; Kind (Def (ValueDef (_, "x", _, Kind (IdentifierExpr "done")))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"
