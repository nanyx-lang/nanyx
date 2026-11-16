module Nanyx.Compiler.Tests.Features.StdLib.Sequences

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The prelude's lazy sequence, `seq(a)`, and ranges as values of it (#192).

let private typeOf (typed: TypedModule) (name: string) = Unifier.tyToDisplayString Ascii typed.Types.[name]

/// `source`, its line endings made `\n`, compiled with no errors.
let private clean (source: string) = compileClean (source.ReplaceLineEndings "\n")

[<Fact>]
let ``A range is a seq of ints, written as the prelude's type is`` () =
    let typed =
        clean """
def bounded = 1..10
def endless = 0..
def written: Seq(int) = 1..3
def aliased: seq(int) -> Seq(int) = { s -> s }
"""
    Assert.Equal("seq(int)", typeOf typed "bounded")
    Assert.Equal("seq(int)", typeOf typed "endless")
    Assert.Equal("seq(int)", typeOf typed "written")
    Assert.Equal("seq(int) -> seq(int)", typeOf typed "aliased")

[<Fact>]
let ``The seq functions are attached to seq, and a pipe finds them`` () =
    let typed =
        clean """
def doubled = (1..10) \seq.map { * 2 } \seq.toList
def piped: [Console] () -> list(int) = { (1..3) \map { x -> x + 1 } \toList }
def firstEvens = (0..) \seq.filter { x -> x % 2 == 0 } \seq.take(3) \Seq.toList
"""
    Assert.Equal("list(int)", typeOf typed "doubled")
    Assert.Equal("list(int)", typeOf typed "firstEvens")

[<Fact>]
let ``A sequence mapped with a function that needs a context carries it`` () =
    let typed =
        clean """
def loud = { () ->
  (1..3) \seq.map { x ->
    println("at {x}")
    x
  }
}
"""
    Assert.Equal("() -> [Console] seq(int)", typeOf typed "loud")

[<Fact>]
let ``A sequence that carries a context is pulled only where the context is`` () =
    let diagnostic =
        diagnosticOf (
            """
def pulled: ([Console] seq(int)) -> list(int) = { s -> s \seq.toList }
""".ReplaceLineEndings "\n"
        )
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
    Assert.Contains("'Seq.toList' requires the context 'Console'", diagnostic.Message)
