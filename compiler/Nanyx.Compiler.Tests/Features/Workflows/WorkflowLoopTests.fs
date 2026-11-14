/// A `for` loop whose body uses a workflow keyword, run by its builder's `for` member (#349).
module Nanyx.Compiler.Tests.Features.WorkflowLoops

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// A builder `opt` whose context has no `for`.
let private optWithoutFor =
    """
context Opt = (
  try: [c] ((#some(a) | #none), ([c] a -> #some(b) | #none)) -> #some(b) | #none
  return: a -> #some(a) | #none
  zero: #some() | #none
)

def opt: [c] ([Opt & c] () -> t) -> t = { block ->
  use Opt(
    try = {
      | #some(value), continue -> continue(value)
      | #none, _ -> #none
    }
    return = { value -> #some(value) }
    zero = #some()
  ) in block()
}
"""

[<Fact>]
let ``A loop whose body uses a keyword in a builder without for is reported at the loop`` () =
    let source =
        optWithoutFor + """
def f = { xs -> opt {
  for xs | x ->
    def y = try x
    y
} }
"""
    let source = source.ReplaceLineEndings "\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
    Assert.Equal(
        "This loop's body uses a workflow keyword, and the context `opt` runs its block under has no `for` to run it: handle the value with a `match` in the loop's body, or give the context a `for`",
        diagnostic.Message)
    Assert.StartsWith("for xs | x ->", textAt source (spanOf diagnostic))

[<Fact>]
let ``A loop whose body uses no keyword runs as a loop, in a builder without for`` () =
    let source =
        optWithoutFor + """
def f = { xs -> opt {
  def first = try #some(1)
  for xs | x -> x + first
  return first
} }
"""
    Assert.Empty((Compiler.compile (source.ReplaceLineEndings "\n")).Diagnostics)
