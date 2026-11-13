module Nanyx.Compiler.Tests.Features.Definitions.LocalRecursion

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A local def is in scope after it, and in its own value only when it is written `rec` (see the functions page).

/// `sumTo`, whose local `go` calls itself, defined with `keyword`.
let private sumTo (keyword: string) =
    let source = $"""
def sumTo: int -> int = {{ n ->
  {keyword} go = {{ i, acc -> if i == 0 then acc else go(i - 1, acc + i) }}
  go(n, 0)
}}
"""
    source.ReplaceLineEndings "\n"

[<Fact>]
let ``A local def that names itself without rec is an error that suggests rec`` () =
    // It used to check, and then fail in the backend (#1013).
    let diagnostic = single (Compiler.compile (sumTo "def"))
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    Assert.Equal("Unknown identifier 'go': a local def can't see itself, so write `rec go = ...` to make it recursive", diagnostic.Message)
    Assert.Equal("go(i - 1, acc + i)", textAt (sumTo "def") (spanOf diagnostic))

[<Fact>]
let ``A rec local def can name itself`` () =
    let typed = compileClean (sumTo "rec")
    Assert.Equal("int -> int", Unifier.tyToString typed.Types.["sumTo"])

[<Fact>]
let ``A local def's value names the definition of that name around it`` () =
    let source = """
def go: int -> int = { i -> i }

def f: int -> int = { n ->
  def go = { i -> go(i) + 1 }
  go(n)
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore
