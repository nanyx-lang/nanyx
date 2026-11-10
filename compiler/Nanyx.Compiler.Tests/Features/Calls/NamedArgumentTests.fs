module Nanyx.Compiler.Tests.Features.Calls.NamedArguments

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private areaSource =
    "def area: (width: int, height: int) -> int = { width, height -> width * height }\n"

/// The one diagnostic `source` has, which must be a named parameter passed by position.
let private positionalForNamed (source: string) =
    let diagnostic = List.exactlyOne (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.PositionalForNamedArgument.Code, diagnostic.Code)
    diagnostic

[<Fact>]
let ``A named parameter passed by position says which parameters to name`` () =
    let source = areaSource + "def a = area(3, 4)\n"
    let diagnostic = positionalForNamed source
    Assert.Equal("'area' takes 'height' and 'width' by name, not by position: write 'height = ...' and 'width = ...'", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``Arguments that each fit one named parameter are named in the call's order, with a fix`` () =
    let source =
        "def pad: (string, width: int, fill: string) -> string = { s, width, fill -> s }\n" +
        "def p = pad(\"7\", 3, \"0\")\n"
    let diagnostic = positionalForNamed source
    Assert.Equal("'pad' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'", diagnostic.Message)
    let fixedSource = applyFix source (List.exactlyOne diagnostic.Fixes)
    Assert.Equal("def pad: (string, width: int, fill: string) -> string = { s, width, fill -> s }\ndef p = pad(\"7\", width = 3, fill = \"0\")\n", fixedSource)
    Assert.Empty((Compiler.compile fixedSource).Diagnostics)

[<Fact>]
let ``A piped call that passes a named parameter by position says which parameters to name`` () =
    let source =
        "def pad: (string, width: int, fill: string) -> string = { s, width, fill -> s }\n" +
        "def p = \"7\" \\pad(3, \"0\")\n"
    let diagnostic = positionalForNamed source
    Assert.Equal("'pad' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'", diagnostic.Message)

[<Fact>]
let ``Only the named parameters the call leaves out are listed`` () =
    let source =
        "def pad: (string, width: int, fill: string) -> string = { s, width, fill -> s }\n" +
        "def p = pad(\"7\", 3, fill = \"0\")\n"
    let diagnostic = positionalForNamed source
    Assert.Equal("'pad' takes 'width' by name, not by position: write 'width = ...'", diagnostic.Message)

[<Fact>]
let ``A record type's constructor called by position says which fields to name`` () =
    let source = "type Point = (x: int, y: int)\ndef p = Point(3, 4)\n"
    let diagnostic = positionalForNamed source
    Assert.Equal("'Point' takes 'x' and 'y' by name, not by position: write 'x = ...' and 'y = ...'", diagnostic.Message)

[<Fact>]
let ``A call that names its named parameters has no diagnostics`` () =
    let named = Compiler.compile (areaSource + "def a = area(width = 3, height = 4)\n")
    Assert.True(named.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{named.Diagnostics}")
    let mixed =
        Compiler.compile (
            "def pad: (string, width: int, fill: string) -> string = { s, width, fill -> s }\n" +
            "def p = pad(\"7\", width = 3, fill = \"0\")\n" +
            "def q = \"7\" \\pad(fill = \"0\", width = 3)\n")
    Assert.True(mixed.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{mixed.Diagnostics}")

[<Fact>]
let ``A record passed whole to a function that takes its fields by name has no diagnostics`` () =
    let result =
        Compiler.compile (
            areaSource +
            "def size = (width = 3, height = 4)\n" +
            "def a = area(size)\n" +
            "def b = size \\area\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
