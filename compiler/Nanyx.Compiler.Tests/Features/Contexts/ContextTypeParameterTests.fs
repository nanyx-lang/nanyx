module Nanyx.Compiler.Tests.Features.ContextTypeParameters

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context's type parameter is a type variable in its body, so it is a single lowercase letter: a longer one is
// reported where it is declared (#888).

[<Fact>]
let ``A context's type parameter longer than one letter is a syntax error at the parameter`` () =
    let source = """context ToText(ab) = (totext: ab -> string)
type Point = (x: int, y: int)

def describe: [ToText(a)] a -> string = { value -> ToText(a).totext(value) }
def Point.totext: Point -> string = { p -> "p" }
"""
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
        Assert.Contains("A type parameter is a single lowercase letter, such as 'a', so 'ab' cannot be one", diagnostic.Message.ReplaceLineEndings " ")
        Assert.Equal(source.IndexOf "ab)", diagnostic.Span.Value.Start)
    | other -> failwith $"Expected one diagnostic: %A{other}"

[<Fact>]
let ``A context with two type parameters type checks where it is declared, required and loaded`` () =
    let source = """context Store(c, i) = (put: (c, i) -> c, size: c -> int)

def fill: [Store(c, i)] c, i, i -> c = { container, x, y -> Store(c, i).put(Store(c, i).put(container, x), y) }

def listStore: Store(list(int), int) = (put = { xs, x -> [x, ...xs] }, size = { xs -> list.length(xs) })
def filled = {
  use listStore
  fill([0], 1, 2)
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore
