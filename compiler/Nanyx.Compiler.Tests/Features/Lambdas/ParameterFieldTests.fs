module Nanyx.Compiler.Tests.Features.Lambdas.ParameterFields

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics `source` has, each of which must be a lambda parameter that names no field.
let private namingNoField (source: string) =
    let diagnostics = (Compiler.compile source).Diagnostics
    Assert.NotEmpty diagnostics
    for diagnostic in diagnostics do
        Assert.Equal(DiagnosticCodes.ParameterNamesNoField.Code, diagnostic.Code)
    diagnostics

[<Fact>]
let ``A lambda's parameter that names no field of a named-field function type is an error at the parameter`` () =
    let source = "def sum: (x: int, y: int) -> int = { a, b -> a + b }\n"
    let diagnostics = namingNoField source
    Assert.Equal<string list>([ "a"; "b" ], diagnostics |> List.map (fun d -> source.Substring(d.Span.Value.Start, d.Span.Value.End - d.Span.Value.Start)))
    Assert.Equal(
        "The parameter 'a' is not a field of the function's input, whose fields a lambda takes by name: write 'x' or 'y', or rename a field with a record pattern, as in '{ (x = a, ...) -> ... }'",
        diagnostics.Head.Message)
    Assert.EndsWith("as in '{ (y = b, ...) -> ... }'", diagnostics.[1].Message)

[<Fact>]
let ``Only the parameter that names no field is reported`` () =
    let source = "def sum: (x: int, y: int) -> int = { x, b -> x + b }\n"
    let diagnostic = List.exactlyOne (namingNoField source)
    Assert.Equal("b", source.Substring(diagnostic.Span.Value.Start, diagnostic.Span.Value.End - diagnostic.Span.Value.Start))

[<Fact>]
let ``A lambda passed where a named-field function type is expected names its fields`` () =
    let source =
        normalized """def apply: ((channel: string, n: int) -> int) -> int = { f -> f(channel = "abc", n = 1) }

def r = apply({ c, n -> c.length + n })
"""
    let diagnostic = List.exactlyOne (namingNoField source)
    Assert.Equal(2, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Parameters named after the fields, in any order, a positional field by position, and a record pattern are fine`` () =
    let source =
        normalized """def sum: (x: int, y: int) -> int = { y, x -> x * 10 + y }
def pad: (string, width: int) -> string = { s, width -> s }
def renamed: (x: int, y: int) -> int = { (x = a, y = b) -> a * 10 + b }
def pair: (int, int) -> int = { a, b -> a + b }
"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A lambda in a list of named-field function types names its fields`` () =
    let source =
        normalized """def applyAll: list(((x: int, y: int) -> int)) -> list(int) = { fs -> [] }

def r = applyAll([{ x, y -> x + y }, { a, b -> a + b }])
"""
    let diagnostics = namingNoField source
    Assert.Equal<string list>([ "a"; "b" ], diagnostics |> List.map (spanOf >> textAt source))

[<Fact>]
let ``A lambda's record pattern may name one field alone and rename another`` () =
    let types = definedTypes "def f: (x: int, y: int) -> int = { (x, y = b) -> x * 10 + b }\n\ndef g = { p: (x: int, y: string) -> match p\n  | (y = s, x) -> \"{s}{x}\"\n}\n"
    Assert.Equal("(x: int, y: int) -> int", Unifier.tyToString types.["f"])
    Assert.Equal("(x: int, y: string) -> string", Unifier.tyToString types.["g"])
