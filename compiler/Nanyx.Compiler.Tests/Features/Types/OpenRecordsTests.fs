/// Open record types written with `...` and `...r` (#471).
module Nanyx.Compiler.Tests.Features.Types.OpenRecords

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``An open record parameter takes a record with more fields`` () =
    let source = """
def g: (x: int, y: int, ...) -> int = { x, y -> x + y }
def data = (x = 1, y = 2, z = 3)
def result = g(data)
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``An open record parameter still needs the fields it lists`` () =
    let source = """
def g: (x: int, y: int, ...) -> int = { x, y -> x + y }
def result = g((x = 1, z = 3))
"""
    let diagnostic = diagnosticOf (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Contains("y", diagnostic.Message)

[<Fact>]
let ``A named row gives the result the other fields of the argument`` () =
    let source = """
def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> p with x = p.x + 1 }
def moved = moveRight((x = 1, y = 2, label = "a"))
def label: string = moved.label
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("(label: string, x: int, y: int)", Unifier.tyToString typed.Types.["moved"])

[<Fact>]
let ``Hover shows an open record annotation with its row`` () =
    let source = "def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> p with x = p.x + 1 }\n"
    // Hovers write a spread as `…` in their default notation.
    Assert.Equal(Some "def moveRight: (x: int, …r) -> (x: int, …r)", hoverType source "moveRight")

[<Fact>]
let ``A with on an open record updates only the fields it lists`` () =
    let source = "def setLabel: (x: int, ...r) -> (x: int, ...r) = { p -> p with label = \"b\" }\n"
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.UnknownMember.Code, diagnostic.Code)
