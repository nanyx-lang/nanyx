module Nanyx.Compiler.Tests.Features.Blocks.DoBlocks

open Xunit
open NyxCompiler

[<Fact>]
let ``A def in a do block is not in scope after it`` () =
    let source = "def f = { ->\n  do\n    def hidden = 5\n    hidden\n  hidden\n}\n"
    let result = Compiler.compile source
    let diagnostic = List.exactlyOne result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    Assert.Equal(source.LastIndexOf "hidden", diagnostic.Span.Value.Start)
