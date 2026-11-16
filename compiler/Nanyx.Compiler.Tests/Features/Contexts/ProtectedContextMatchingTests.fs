module Nanyx.Compiler.Tests.Features.ProtectedContextMatching

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A plain context matches any context with its members, but a protected or private one matches only itself (#1443):
// otherwise another context with the same members could be loaded in its place.

let private forged (declaration: string) =
    $"""{declaration}
context Fake = (commit: () -> ())

def needsTx: [Tx] () -> () = {{ commit() }}

def main = {{
  use Fake(commit = {{ () }})
  needsTx()
}}
"""

[<Fact>]
let ``Another context with the same members does not serve for a protected one`` () =
    let result = Compiler.compile ((forged "context Tx = protected (commit: () -> ())").ReplaceLineEndings "\n")
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal(
        "'needsTx' requires the context 'Tx', which is not available here: 'Tx' is protected, so only an instance its own module makes serves for it, not another context with the same members",
        diagnostic.Message)

[<Fact>]
let ``The error is at the call that requires the protected context`` () =
    let code, text = singleError ((forged "context Tx = protected (commit: () -> ())").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("needsTx", text)

[<Fact>]
let ``Another context with the same members does not serve for a private one`` () =
    let result = Compiler.compile ((forged "context Tx = private (commit: () -> ())").ReplaceLineEndings "\n")
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("'Tx' is private", diagnostic.Message)

[<Fact>]
let ``Another context with the same members does not serve for a scoped one`` () =
    let code, _ = singleError ((forged "scoped context Tx = protected (commit: () -> ())").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)

[<Fact>]
let ``A protected context's own instance serves for it`` () =
    compileClean (
        """context Tx = protected (commit: () -> ())

def needsTx: [Tx] () -> () = { commit() }
def viaTx: [Tx] () -> () = { needsTx() }

def main = {
  use Tx(commit = { () })
  needsTx()
}
"""
            .ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``A protected context serves for a plain one whose members it has`` () =
    compileClean (
        """context Tx = (commit: () -> ())
context Strong = protected (commit: () -> ())

def needsTx: [Tx] () -> () = { commit() }
def viaStrong: [Strong] () -> () = { needsTx() }

def main = {
  use Strong(commit = { () })
  needsTx()
}
"""
            .ReplaceLineEndings "\n")
    |> ignore
