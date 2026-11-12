module Nanyx.Compiler.Tests.Features.ContextVariables

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A single lowercase letter in a context list is a context variable: `c` in `[c] ([c] int -> int), int -> int`. A
// call binds it to the contexts of the function passed for it, and requires them.

let private apply =
    """
import nanyx/console

def apply: [c] ([c] int -> int), int -> int = { f, x -> f(x) }
"""

let private withApply (source: string) = $"{apply}{source}"

let private hoverType (source: string) (text: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let offset = source.ReplaceLineEndings("\n").IndexOf text
    Hover.typeAt Unicode result offset |> Option.map _.Type

[<Fact>]
let ``A context variable the def does not bind is an error`` () =
    let code, text =
        singleError
            """
def g: [d] ([c] int -> int) -> int = { f -> 1 }
"""
    Assert.Equal(DiagnosticCodes.UnboundContextVariable.Code, code)
    Assert.Equal("c", text)

[<Fact>]
let ``A call requires the contexts of the function passed for a context variable`` () =
    let code, text =
        singleError (
            withApply
                """
def run: ([Console] int -> int) -> int = { g -> apply(g, 1) }
"""
        )
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("apply", text)

[<Fact>]
let ``A call through a context variable checks where the def declares the context`` () =
    compileClean (
        withApply
            """
def run: [Console] ([Console] int -> int) -> int = { g -> apply(g, 1) }
"""
    )
    |> ignore

[<Fact>]
let ``A context variable bound to a pure function requires nothing`` () =
    compileClean (
        withApply
            """
def four = apply({ x -> x + 1 }, 3)

def five: int -> int = { n -> apply({ x -> x + 1 }, n) }
"""
    )
    |> ignore

[<Fact>]
let ``A context variable can be passed on to another`` () =
    compileClean (
        withApply
            """
def run: [d] ([d] int -> int) -> int = { g -> apply(g, 1) }

def main: [Console] ([Console] int -> int) -> int = { g -> run(g) }
"""
    )
    |> ignore

[<Fact>]
let ``Hover at a call shows the context variable filled in`` () =
    let source =
        withApply
            """
def run: [Console] ([Console] int -> int) -> int = { g -> apply(g, 1) }
"""
    Assert.Equal(Some "def apply: [Console] ([Console] int -> int), int -> int", hoverType source "apply(g")

[<Fact>]
let ``An unannotated def infers the contexts a context variable is bound to`` () =
    let source =
        withApply
            """
def run = { (g: [Console] int -> int) -> apply(g, 1) }
"""
    Assert.Equal(Some "def run: [Console] ([Console] int -> int) -> int", hoverType source "run =")