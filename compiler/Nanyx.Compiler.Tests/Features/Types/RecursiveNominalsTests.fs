module Nanyx.Compiler.Tests.Features.Types.RecursiveNominals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A protected or private type that names itself in its definition (#1663).

let private typeOf (typed: TypedModule) (name: string) = Unifier.tyToDisplayString Ascii typed.Types.[name]

/// `source`, its line endings made `\n`, compiled with no errors.
let private clean (source: string) = compileClean (source.ReplaceLineEndings "\n")

/// The code of the one diagnostic `source`, its line endings made `\n`, compiles to, and the text it is reported at.
let private error (source: string) = singleError (source.ReplaceLineEndings "\n")

let private stream = """
type Stream(a) = [k] protected (next: [k] () -> #next(a, Stream(a)) | #done)

def loud: [Console] Stream(int) = Stream(next = {
  println("one")
  #next(1, Stream(next = { #done }))
})
"""

[<Fact>]
let ``A protected type names itself in a field`` () =
    let typed =
        clean """
type Node = protected (value: int, rest: #some(Node) | #none)

def n = Node(value = 1, rest = #some(Node(value = 2, rest = #none)))

def sum: Node -> int = { node ->
  match node.rest
    | #some(next) -> node.value + sum(next)
    | #none -> node.value
}

def total = sum(n)
"""
    Assert.Equal("Node", typeOf typed "n")
    Assert.Equal("int", typeOf typed "total")

[<Fact>]
let ``A generic protected type names itself at its parameters`` () =
    let typed =
        clean """
type Stream(a) = protected (next: () -> #next(a, Stream(a)) | #done)

def u = Stream(next = { #next(1, Stream(next = { #done })) })

def firstRest: Stream(a) -> #some(Stream(a)) | #none = { s ->
  match s.next()
    | #next(_, rest) -> #some(rest)
    | #done -> #none
}
"""
    Assert.Equal("Stream(int)", typeOf typed "u")

[<Fact>]
let ``A private type names itself`` () =
    let typed =
        clean """
type Chain = private (link: #some(Chain) | #none)

def two = Chain(link = #some(Chain(link = #none)))

def length: Chain -> int = { chain ->
  match chain.link
    | #some(next) -> 1 + length(next)
    | #none -> 1
}
"""
    Assert.Equal("Chain -> int", typeOf typed "length")

[<Fact>]
let ``A type generic in contexts that names itself carries its own contexts there`` () =
    let typed =
        clean (
            stream
            + """
def rest: [c] ([c] Stream(a)), ([c] Stream(a)) -> [c] Stream(a) = { s, fallback ->
  match s.next()
    | #next(_, tail) -> tail
    | #done -> fallback
}

def loudRest: [Console] () -> [Console] Stream(int) = { rest(loud, loud) }
"""
        )
    Assert.EndsWith("([c] Stream(a)), ([c] Stream(a)) -> [c] Stream(a)", typeOf typed "rest")

[<Fact>]
let ``A def whose value nests constructors of a recursive type generic in contexts keeps its declared contexts`` () =
    // #1701: the inner constructor's contexts are the outer's tail's, `d`, and its pure lambda solved `d` to a made-up
    // variable, which the def was then generalised without.
    let typed =
        clean
            """type Rq(a) = [k] protected (next: [k] () -> #next(a, Rq(a)) | #done)

def v1: ([d] int -> b) -> [d] Rq(b) = { f -> Rq(next = { #next(f(1), Rq(next = { #done })) }) }
"""
    Assert.Equal("([d] int -> b) -> [d] Rq(b)", typeOf typed "v1")

[<Fact>]
let ``The rest of a value of a type generic in contexts is not pure`` () =
    let diagnostic =
        diagnosticOf (
            stream
            + """
def pulled: Stream(int) -> int = { _ -> 0 }

def pullRest: [Console] ([Console] Stream(int)) -> int = { s ->
  match s.next()
    | #next(_, tail) -> pulled(tail)
    | #done -> 0
}
""".ReplaceLineEndings "\n"
        )
    // The rest's type is solved after the call it is passed to is checked, so the two are made the same type, not one
    // given for the other. The mismatch is the two nominal types', not the contexts they carry (#1677).
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, diagnostic.Code)
    Assert.Equal(
        "Context mismatch: '[Console] Stream(int)' vs 'Stream(int)', where only the first carries 'Console': write the same contexts in front of both, or a context variable for any",
        diagnostic.Message
    )
