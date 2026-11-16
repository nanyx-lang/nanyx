module Nanyx.Compiler.Tests.Features.MemberlessContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A protected or private context with no members, a capability token such as `context Admin = private ()`, is
// available only where a signature declares it, a function supplies it to a lambda, or a `use` loads an instance of
// it: no member in scope says it is there (#1479).

let private needsTx (declaration: string) (rest: string) =
    let source = $"""{declaration}

def needsTx: [Tx] () -> () = {{ () }}
{rest}"""
    source.ReplaceLineEndings "\n"

[<Fact>]
let ``A memberless protected context nothing supplies is not available`` () =
    let annotated = needsTx "context Tx = protected ()" """
def main: () -> () = {
  needsTx()
}
"""
    Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, "needsTx"), singleError annotated)
    let inferred = needsTx "context Tx = protected ()" """
def main = { needsTx() }
"""
    Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, "needsTx"), singleError inferred)

[<Fact>]
let ``Nor is a memberless private one`` () =
    let source = needsTx "context Admin = private ()\ncontext Tx = private ()" """
def main: () -> () = { needsTx() }
"""
    let diagnostic = (Compiler.compile source).Diagnostics |> Assert.Single
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal("'needsTx' requires the context 'Tx', which is not available here", diagnostic.Message)

[<Fact>]
let ``A signature, a function that supplies it to a lambda, or a use makes a memberless context available`` () =
    needsTx "context Tx = protected ()" """
def declared: [Tx] () -> () = { needsTx() }
def withTx: [c] ([Tx, c] () -> a) -> a = { f -> use Tx(()) in f() }
def handled = withTx { needsTx() }
def loaded: () -> () = {
  use Tx(())
  needsTx()
}
"""
    |> compileClean
    |> ignore

[<Fact>]
let ``A plain memberless context is any context's, so it is always available`` () =
    needsTx "context Tx = ()" """
def main: () -> () = { needsTx() }
"""
    |> compileClean
    |> ignore
