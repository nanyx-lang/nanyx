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
    Assert.Equal("Unknown attribute '@inline'", Diagnostics.headline diagnostic)
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
    Assert.Equal("Unexpected attribute argument '@entry'", Diagnostics.headline diagnostic)
    Assert.Equal("It takes no argument", diagnostic.Message)
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

def logged: [c] ([Logged, c] () -> int) -> int = { block ->
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
    let source = "context C = (\n  @customKeywordType(#later)\n  step: int\n)"
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnknownKeywordKind.Code, diagnostic.Code)
    Assert.Equal("Unknown keyword kind '#later'", Diagnostics.headline diagnostic)
    Assert.Equal("The kinds are `#bind`, `#bindF`, `#delay`, `#yield`", diagnostic.Message)
    Assert.Equal((16, 42), (diagnostic.Span.Value.Start, diagnostic.Span.Value.End))

[<Theory>]
[<InlineData("delay", "start: [c] ([c] () -> a) -> Task(a)")>]
[<InlineData("delay", "start: [Async] (() -> a) -> Task(a)")>]
[<InlineData("bindF", "defer: [c] (([c] () -> ()), ([c] () -> int)) -> int")>]
let ``A #delay or #bindF member whose type takes a function of no parameters is accepted`` (kind: string, field: string) =
    let source =
        $"""type Task(a) = (value: a)
context Async = (x: int)
context C = (
  @customKeywordType(#{kind})
  {field}
)
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Theory>]
[<InlineData("delay", "start", "int -> int")>]
[<InlineData("delay", "start", "int")>]
[<InlineData("delay", "start", "(int -> int) -> int")>]
[<InlineData("delay", "start", "((() -> int), int) -> int")>]
[<InlineData("bindF", "defer", "(int, (int -> int)) -> int")>]
[<InlineData("bindF", "defer", "(() -> int) -> int")>]
let ``A #delay or #bindF member whose type doesn't take a function of no parameters is an error at the attribute`` (kind: string, name: string, memberType: string) =
    let source = $"context C = (\n  @customKeywordType(#{kind})\n  {name}: {memberType}\n)"
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.KeywordMemberType.Code, diagnostic.Code)
    Assert.StartsWith($"'{name}' is a `#{kind}` keyword", diagnostic.Message)
    Assert.Equal(16, diagnostic.Span.Value.Start)

[<Fact>]
let ``A top-level def can be a #delay keyword`` () =
    let source =
        """def twice: (() -> int) -> int = { work -> work() + work() }

@customKeywordType(#delay)
def run: (() -> int) -> int = { work -> twice(work) }

def result = run! 21
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Theory>]
[<InlineData("bind")>]
[<InlineData("bindF")>]
[<InlineData("yield")>]
let ``A top-level def can't be a keyword of any other kind`` (kind: string) =
    let source = $"@customKeywordType(#{kind})\ndef run: (() -> int) -> int = {{ work -> work() }}\n"
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnknownKeywordKind.Code, diagnostic.Code)
    Assert.StartsWith("A definition can only be a `#delay` keyword", diagnostic.Message)

[<Fact>]
let ``A #delay def whose type doesn't take a function of no parameters is an error at the attribute`` () =
    let source = "@customKeywordType(#delay)\ndef run: int -> int = { n -> n }\n"
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.KeywordMemberType.Code, diagnostic.Code)
    Assert.StartsWith("'run' is a `#delay` keyword", diagnostic.Message)
    Assert.Equal(0, diagnostic.Span.Value.Start)

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
let ``An attribute on a field of a record type's definition is kept for the type's description`` () =
    let typed =
        compileClean (
            normalized
                """type T = (
  @json(name = "user_id")
  id: int
  @range(1, 10)
  @deprecated
  level: int
)
"""
        )
    let attributes = typed.TypeDefs.["T"].FieldAttributes
    Assert.Equal<FieldAttribute list>([ { Name = "json"; Arguments = [ "name", "user_id" ] } ], attributes.["id"])
    Assert.Equal<FieldAttribute list>(
        [ { Name = "range"; Arguments = [ "0", "1"; "1", "10" ] }; { Name = "deprecated"; Arguments = [] } ],
        attributes.["level"]
    )

[<Fact>]
let ``An attribute on a field of a record type that is not a definition is misplaced`` () =
    let source =
        normalized
            """def f: (
  @json(name = "x")
  a: int
) -> int = { r -> r.a }
"""
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.MisplacedAttribute.Code, diagnostic.Code)
    Assert.Equal("It can only come before a field of a record type's definition or a member of a context declaration", diagnostic.Message)

[<Fact>]
let ``An attribute on a field takes literals`` () =
    let source =
        normalized
            """def name = "id"

type T = (
  @json(name = name)
  id: int
)
"""
    let diagnostic = (Compiler.compile source).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, diagnostic.Code)
    Assert.StartsWith("On a field, it takes literals", diagnostic.Message)
