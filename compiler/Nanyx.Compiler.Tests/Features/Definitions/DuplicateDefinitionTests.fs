module Nanyx.Compiler.Tests.Features.Definitions.DuplicateDefinitions

open Xunit
open NyxCompiler
open Parser.Program

[<Fact>]
let ``A top-level name defined twice is reported at the second definition, pointing back at the first`` () =
    let result =
        Compiler.compile """def x = 1
def x = 2"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Equal("The name 'x' is already defined in this module", diagnostic.Message)
    Assert.Equal((1, 4), (diagnostic.Span.Value.StartLine, diagnostic.Span.Value.StartColumn))
    let related, _ = Assert.Single(diagnostic.Related)
    Assert.Equal((0, 4), (related.StartLine, related.StartColumn))

[<Fact>]
let ``A name defined again at another type is a duplicate, not a type mismatch`` () =
    let result =
        Compiler.compile """def x = 1
def x = "two"
def y: int = x"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)

[<Fact>]
let ``Each repeat of a name is reported`` () =
    let result =
        Compiler.compile """def x = 1
def x = 2
def x = 3"""
    Assert.Equal<string list>(
        [ DiagnosticCodes.DuplicateDefinition.Code; DiagnosticCodes.DuplicateDefinition.Code ],
        result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code))
    Assert.Equal<int list>([ 1; 2 ], result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Span.Value.StartLine))

[<Fact>]
let ``An exported def and a private one of the same name are duplicates`` () =
    let result =
        Compiler.compile """export def x = 1
def x = 2"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)

[<Fact>]
let ``A type defined twice is reported`` () =
    let result =
        Compiler.compile """type Point = (x: int)
type Point = (y: int)"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal("The type 'Point' is already defined in this module", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Attached defs of one name on different types are not duplicates`` () =
    let result =
        Compiler.compile """type Person = (name: string)
type Pet = (name: string)
def Person.describe: Person -> string = { p -> p.name }
def Pet.describe: Pet -> string = { p -> p.name }"""
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Attached defs of one name on built-in types are not duplicates`` () =
    let result =
        Compiler.compile """def int.describe: int -> string = { n -> "a number" }
def string.describe: string -> string = { s -> "some text" }
def list.describe: list(int) -> string = { xs -> "a list" }"""
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An attached def defined twice on one type is reported`` () =
    let result =
        Compiler.compile """type Person = (name: string)
def Person.describe: Person -> string = { p -> p.name }
def Person.describe: Person -> string = { p -> "someone" }"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.DuplicateDefinition.Code, diagnostic.Code)
    Assert.Equal(2, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Defs of one name in different module blocks are not duplicates`` () =
    let result =
        Compiler.compile """module first =
  export def x = 1
module second =
  export def x = 2
def x = 3"""
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A local def written twice in a block still shadows the first`` () =
    let result =
        Compiler.compile """def f = { ->
  def x = 1
  def x = 2
  x
}"""
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
