module Nanyx.Compiler.Tests.Features.Attributes

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Entry attribute appends a call of the host's run with the entry point`` () =
    let source =
        """module main
@entry
def main = {
  println("Hello, World!")
}
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let entryCalls =
        result.Typed.Value.Module
        |> List.filter (function Kind (Expr (Kind (FunctionCall("__host.run", _, [ Kind (IdentifierExpr "main") ])))) -> true | _ -> false)
    Assert.Equal(1, entryCalls.Length)
    let hasAttribute = result.Typed.Value.Module |> List.exists (function Kind (Attribute _) -> true | _ -> false)
    Assert.False(hasAttribute)

[<Fact>]
let ``Unknown attribute is an error, reported at the attribute`` () =
    let result = Compiler.compile "def g = 2\n@inline\ndef f = { 1 }"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("Unknown attribute '@inline'", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((10, 17, 1, 0), (span.Start, span.End, span.StartLine, span.StartColumn))

[<Fact>]
let ``Entry attribute must precede a definition, and is reported at the attribute`` () =
    let result = Compiler.compile "@entry\n1 + 1"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("must be followed by a definition", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((0, 6), (span.Start, span.End))

[<Fact>]
let ``Entry attribute with an argument is an error, reported at the argument`` () =
    let result = Compiler.compile "@entry(#now)\ndef main = { 1 }"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, diagnostic.Code)
    Assert.Contains("Attribute '@entry' takes no argument", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((7, 11), (span.Start, span.End))

[<Fact>]
let ``A context member's keyword kind is accepted, and the builder's block compiles`` () =
    let source =
        """context Logged = (
  @customKeywordType(#bind)
  step: [c] (int, ([c] int -> int)) -> int
  return: int -> int
)

def logged: [c] ([Logged & c] () -> int) -> int = { block ->
  use Logged(step = { value, continue -> continue(value + 1) }, return = { value -> value }) in block()
}

def result = logged {
  def x = step! 1
  return x * 10
}
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An unknown keyword kind is an error, reported at the attribute`` () =
    let source = "context C = (\n  @customKeywordType(#bindF)\n  step: int\n)"
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnknownKeywordKind.Code, diagnostic.Code)
    Assert.Equal("Unknown keyword kind '#bindF': the kinds are `#bind`", diagnostic.Message)
    Assert.Equal((16, 42), (diagnostic.Span.Value.Start, diagnostic.Span.Value.End))

[<Fact>]
let ``A keyword type without a kind is an error`` () =
    let diagnostic = (Compiler.compile "context C = (\n  @customKeywordType\n  step: int\n)").Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnknownKeywordKind.Code, diagnostic.Code)
    Assert.Contains("takes the keyword's kind", diagnostic.Message)

[<Fact>]
let ``An unknown attribute on a context's member is an error`` () =
    let diagnostic = (Compiler.compile "context C = (\n  @inline\n  step: int\n)").Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnknownAttribute.Code, diagnostic.Code)
    Assert.Equal((16, 23), (diagnostic.Span.Value.Start, diagnostic.Span.Value.End))

[<Fact>]
let ``An attribute on a field of a type that is not a context is misplaced`` () =
    let diagnostic = (Compiler.compile "type T = (\n  @customKeywordType(#bind)\n  x: int\n)").Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.MisplacedAttribute.Code, diagnostic.Code)
    Assert.Equal("Attribute '@customKeywordType' can only come before a member of a context declaration", diagnostic.Message)
