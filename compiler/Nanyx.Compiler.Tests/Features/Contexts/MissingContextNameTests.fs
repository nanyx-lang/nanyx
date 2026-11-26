module Nanyx.Compiler.Tests.Features.MissingContextNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #2806: a missing context is named with its type arguments, in the headline as in the message.

[<Fact>]
let ``A missing context of a type is named with it`` () =
    let source = normalized """type P = protected (x: int)

def p: P = P(x = 1)
println(p)
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal("Missing context 'ToString(P)'", Diagnostics.headline diagnostic)
    Assert.Equal(
        "'println' requires the context 'ToString(P)', which is not loaded here, and 'P.toString' is not defined: define it, or load an instance with `use`",
        diagnostic.Message)

[<Fact>]
let ``A missing context of a type variable is named with it`` () =
    let source = normalized """def h: (list(a)) -> list(a) = { xs -> list.sort(xs) }
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal("Missing context 'Ord(a)'", Diagnostics.headline diagnostic)
    Assert.Equal("'list.sort' requires the context 'Ord(a)', which is not available here: add [Ord(a)] to the type of 'h'", diagnostic.Message)

[<Fact>]
let ``A missing context an operator needs is named with its operand's type`` () =
    let source = normalized """def g: (a, a) -> bool = { x, y -> x < y }
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal("Missing context 'Ord(a)'", Diagnostics.headline diagnostic)

[<Fact>]
let ``A missing context at a type nothing has settled yet is named alone`` () =
    // Its argument is a variable the reader never wrote, so writing it would say nothing.
    let source = normalized """def f: [Console] (a) -> () = { x -> println(x) }
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal("Missing context 'ToString'", Diagnostics.headline diagnostic)
    Assert.Equal("'println' requires the context 'ToString', which is not available here", diagnostic.Message)
