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

[<Fact>]
let ``A context variable the function a def returns requires is bound by that function`` () =
    // Whoever calls the returned function binds `c`, so the lambda that makes it may call `f` and `g`.
    compileClean
        """
def compose: ([c] a -> b), ([c] b -> d) -> [c] a -> d = { f, g -> { x -> g(f(x)) } }
"""
    |> ignore

[<Fact>]
let ``A def that calls a function for a variable only the function it returns requires must still declare it`` () =
    let code, text =
        singleError
            """
def runNow: ([c] int -> int) -> [c] int -> int = { f ->
  def n = f(1)
  { x -> x + n }
}
"""
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("f", text)

[<Fact>]
let ``A value that is not a lambda cannot write only contexts`` () =
    let code, message, text =
        onlyDiagnostic
            """
import nanyx/console

def shout: [Console] string -> () = { s -> println(s ++ "!") }

def greet: [Console] = { name -> "hello " ++ name } >> shout
"""
    Assert.Equal(DiagnosticCodes.ContextsWithoutType.Code, code)
    Assert.Equal("'greet' is not a lambda, so it does not run in the contexts its annotation writes: write them on its function type, as in '[Console] string -> ()'", message)
    Assert.Equal("[Console]", text)
