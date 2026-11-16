module Nanyx.Compiler.Tests.Features.ContextGenericTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `type Thunk(a) = [k] protected (...)`: a type generic in contexts. A use writes the contexts in front of it,
// `[Console] Thunk(int)`, or none for the pure one, and calling the functions inside requires them (#1525).

let private thunk = """type Thunk(a) = [k] protected (get: [k] () -> a)

def pure: Thunk(int) = Thunk(get = { 1 })
def loud: [Console] Thunk(int) = Thunk(get = {
  println("got")
  2
})
def force: [c] ([c] Thunk(a)) -> a = { t -> t.get() }
"""

[<Fact>]
let ``A type generic in contexts carries those of the functions it is made with`` () =
    let source =
        thunk
        + """
def map: ([c] Thunk(a)), ([d] a -> b) -> [c, d] Thunk(b) = { t, f -> Thunk(get = { f(t.get()) }) }
def made = Thunk(get = { 3 })
def mapped = map(loud, { x -> x + 1 })
"""
    let typed = compileClean (normalized source)
    let shown name = Unifier.tyToDisplayString Ascii typed.Types.[name]
    Assert.Equal("Thunk(int)", shown "pure")
    Assert.Equal("[Console] Thunk(int)", shown "loud")
    Assert.Equal(Some "def force: [c] ([c] Thunk(a)) -> a", hoverType (normalized source) "force")
    Assert.Equal("([c] Thunk(a)), ([d] a -> b) -> [c, d] Thunk(b)", shown "map")
    Assert.Equal("Thunk(int)", shown "made")
    Assert.Equal("[Console] Thunk(int)", shown "mapped")

[<Fact>]
let ``A value with fewer contexts fits where one with more is expected`` () =
    let source =
        thunk
        + """
def widened: [Console] Thunk(int) = pure
def run: [Console] () -> int = { force(loud) + force(pure) }
"""
    compileClean (normalized source) |> ignore

[<Fact>]
let ``A value with more contexts is NYX0227 where one with fewer is expected`` () =
    let source =
        thunk
        + """
def narrowed: Thunk(int) = loud
"""
    Assert.Equal((DiagnosticCodes.ContextsNotAllowed.Code, "loud"), singleError (normalized source))

[<Fact>]
let ``Pulling an effectful value in pure code is NYX0211`` () =
    let source =
        thunk
        + """
def pulled: () -> int = { force(loud) }
"""
    Assert.Equal((DiagnosticCodes.UndeclaredContext.Code, "force"), singleError (normalized source))

[<Fact>]
let ``Calling the function of an effectful value in pure code is NYX0211`` () =
    let source =
        thunk
        + """
def pulled: () -> int = { loud.get() }
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)

[<Fact>]
let ``Pure code can pull a pure value`` () =
    let source =
        thunk
        + """
def pulled: () -> int = { force(pure) + pure.get() }
"""
    compileClean (normalized source) |> ignore

[<Fact>]
let ``Contexts in front of a type not declared with a context variable are NYX0253`` () =
    let source = """type Point = protected (x: int)

def p: [Console] Point = Point(x = 1)
"""
    let diagnostic = single (Compiler.compile (normalized source))
    Assert.Equal(DiagnosticCodes.ContextsOnValueType.Code, diagnostic.Code)
    Assert.Contains("doesn't take contexts", diagnostic.Message)

[<Fact>]
let ``A type has one context variable, and not a scoped context's scope too`` () =
    let source = """scoped context Tx = private ()

type Two = [k, j] protected (get: [k] () -> int)
type Both = [Tx, k] protected (get: [k] () -> int)
"""
    let result = Compiler.compile (normalized source)
    let codes = result.Diagnostics |> List.map _.Code |> List.distinct
    Assert.Equal<string list>([ DiagnosticCodes.InvalidTypeScope.Code ], codes)
    Assert.Equal(2, result.Diagnostics.Length)