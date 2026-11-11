module Nanyx.Compiler.Tests.Features.Blocks.Blocks

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A block gives back the value of its last statement, so it has to end with an expression.

/// The one diagnostic `source` has, which must be a block without a result: the text it is reported at.
let private withoutResult (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.BlockWithoutResult.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``a lambda whose body is only a def is an error at the def`` () =
    Assert.Equal("def r = i + 1", withoutResult "def f = { i ->\n  def r = i + 1\n}\n")

[<Fact>]
let ``a block that ends with a def after an expression is an error`` () =
    Assert.Equal("def q = 2", withoutResult "def f = { i ->\n  def r = i + 1\n  r\n  def q = 2\n}\n")

[<Fact>]
let ``a nested lambda that forgets to give back its local is an error`` () =
    Assert.Equal("def r = rules\n      \\list.map { x -> x + i }", withoutResult "def g = { rules ->\n  { i ->\n    def r = rules\n      \\list.map { x -> x + i }\n  }\n}\n")

[<Fact>]
let ``an empty block and a block that ends with unit are fine`` () =
    let typed = compileClean "def f = { }\ndef g = { i ->\n  def r = i + 1\n  ()\n}\n"
    Assert.Equal("() -> ()", Unifier.tyToString typed.Types.["f"])
    Assert.Equal("int -> ()", Unifier.tyToString typed.Types.["g"])
