/// A def attached to a context is in scope by its bare name wherever the context is available (#2493, see
/// `AttachedContextDefs`).
module Nanyx.Compiler.Tests.Features.AttachedContextDefs

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// Two contexts, each with a def named `hello` attached.
let private twoGreeters =
    """context Greeter = protected ()

def Greeter.hello: [Greeter] string -> string = { s -> "hello {s}" }

context Waver = protected ()

def Waver.hello: [Waver] string -> string = { s -> "wave {s}" }
"""

[<Fact>]
let ``A bare name two available contexts attach a def by is ambiguous`` () =
    let source =
        withFinalNewline (
            twoGreeters
            + """
def both = { _ ->
  use Greeter()
  use Waver()
  hello("x")
}"""
        )
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.AmbiguousAttachedFunction.Code, code)
    Assert.StartsWith("'hello' could be 'Greeter.hello' and 'Waver.hello'", message)
    Assert.Equal("hello", at)

[<Fact>]
let ``A bare name reaches the one of two contexts that is available`` () =
    let source =
        withFinalNewline (
            twoGreeters
            + """
def one = { _ ->
  use Waver()
  hello("x")
}"""
        )
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``In a def that doesn't write its type, a bare name two contexts attach a def by is ambiguous`` () =
    let source = withFinalNewline (twoGreeters + "\ndef greetsBack = { name: string -> hello(name) }")
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.AmbiguousAttachedFunction.Code, code)
    Assert.Equal("hello", at)

[<Fact>]
let ``In a def that doesn't write its type, a bare name means the attached def's whole name, which requires its context`` () =
    let source =
        withFinalNewline
            """context Greeter = protected ()

def Greeter.hello: [Greeter] string -> string = { s -> "hello {s}" }

def greetsBack = { name: string -> hello(name) ++ "!" }"""
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'Greeter.hello' requires the context 'Greeter', which is not available here", message.Split('\n').[0])
    Assert.Equal("hello", at)

