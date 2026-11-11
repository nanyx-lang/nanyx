module Nanyx.Compiler.Tests.Features.Definitions.DuplicateDefinitions

open Xunit
open NyxCompiler
open Parser.Program

[<Fact>]
let ``A top-level name defined twice is reported at the second definition, pointing back at the first`` () =
    let result = Compiler.compile "def x = 1\ndef x = 2"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal("The name 'x' is already defined in this module", diagnostic.Message)
    Assert.Equal((1, 4), (diagnostic.Span.Value.StartLine, diagnostic.Span.Value.StartColumn))
    let related, _ = Assert.Single(diagnostic.Related)
    Assert.Equal((0, 4), (related.StartLine, related.StartColumn))

[<Fact>]
let ``A name defined again at another type is a duplicate, not a type mismatch`` () =
    let result = Compiler.compile "def x = 1\ndef x = \"two\"\ndef y: int = x"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)

[<Fact>]
let ``Each repeat of a name is reported`` () =
    let result = Compiler.compile "def x = 1\ndef x = 2\ndef x = 3"
    Assert.Equal<string list>(
        [ DiagnosticCodes.DuplicateDefinition.Code; DiagnosticCodes.DuplicateDefinition.Code ],
        result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code))
    Assert.Equal<int list>([ 1; 2 ], result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Span.Value.StartLine))

[<Fact>]
let ``An exported def and a private one of the same name are duplicates`` () =
    let result = Compiler.compile "export def x = 1\ndef x = 2"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)

[<Fact>]
let ``A type defined twice is reported`` () =
    let result = Compiler.compile "type Point = (x: int)\ntype Point = (y: int)"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal("The type 'Point' is already defined in this module", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Attached defs of one name on different types are not duplicates`` () =
    let source =
        "type Person = (name: string)\n" +
        "type Pet = (name: string)\n" +
        "def Person.describe: Person -> string = { p -> p.name }\n" +
        "def Pet.describe: Pet -> string = { p -> p.name }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An attached def defined twice on one type is reported`` () =
    let source =
        "type Person = (name: string)\n" +
        "def Person.describe: Person -> string = { p -> p.name }\n" +
        "def Person.describe: Person -> string = { p -> \"someone\" }"
    let diagnostic = Assert.Single(Compiler.compile(source).Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal(2, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Defs of one name in different module blocks are not duplicates`` () =
    let source =
        "module first =\n" +
        "  export def x = 1\n" +
        "module second =\n" +
        "  export def x = 2\n" +
        "def x = 3"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A local def written twice in a block still shadows the first`` () =
    let result = Compiler.compile "def f = { ->\n  def x = 1\n  def x = 2\n  x\n}"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
