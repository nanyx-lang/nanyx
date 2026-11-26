/// Syntax errors (NYX0001): the headline names the token the parser found, and the body what it expected.
module Nanyx.Compiler.Tests.Features.Diagnostics.SyntaxErrors

open Xunit
open NyxCompiler

/// The headline and body of the one diagnostic compiling `source` gives.
let private headlineAndBody (source: string) =
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    Diagnostics.headline diagnostic, diagnostic.Message

// The token found was in the body, "Expected `)`, found `]`", and the headline was the title alone (#2156).
[<Fact>]
let ``A syntax error's headline names the token found, and its body what was expected`` () =
    Assert.Equal(("Syntax error '`]`'", "Expected `)`"), headlineAndBody "def x = (1 + 2]\n")

[<Fact>]
let ``A syntax error at the end of the file says so in its body`` () =
    Assert.Equal(("Syntax error", "Expected `)`, found the end of the file"), headlineAndBody "def x = (1 + 2")
