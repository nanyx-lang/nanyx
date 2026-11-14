module Nanyx.Compiler.Tests.Features.ImportHints

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic compiling `source` gives: its code, its message, the text its span covers, and the titles of
/// its fixes.
let private reported (source: string) =
    let diagnostic = diagnosticOf source
    diagnostic.Code, diagnostic.Message, textAt source (spanOf diagnostic), diagnostic.Fixes |> List.map _.Title

[<Fact>]
let ``An unknown function a standard-library module exports says which import brings it in`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownFunction.Code,
         """Unknown function 'println'. 'println' is defined in 'nanyx/console': add `import nanyx/console`""",
         "println",
         [ "Add `import nanyx/console`" ]),
        reported "def c = println(1)\n")

[<Fact>]
let ``An unknown qualified name says which import brings it in`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownFunction.Code,
         """Unknown function 'string.contains'. 'string.contains' is defined in 'nanyx/text': add `import nanyx/text`""",
         "string.contains",
         [ "Add `import nanyx/text`" ]),
        reported """def a = string.contains("hello world", "world")""")

[<Fact>]
let ``A name qualified by a module's name says to import the module under that name`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownFunction.Code,
         """Unknown function 'math.abs'. 'abs' is defined in 'nanyx/math': add `import nanyx/math as math`""",
         "math.abs",
         [ "Add `import nanyx/math as math`" ]),
        reported "def e = math.abs(1)\n")

[<Fact>]
let ``An unknown identifier says which import brings it in, instead of asking about names in scope`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownIdentifier.Code,
         """Unknown identifier 'abs'. 'abs' is defined in 'nanyx/math': add `import nanyx/math`""",
         "abs",
         [ "Add `import nanyx/math`" ]),
        reported "def f = abs\n")

[<Fact>]
let ``An unknown context in a signature says which import brings it in, at the context's name`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownContext.Code,
         """Unknown context 'Console' in the signature of 'g'. 'Console' is defined in 'nanyx/console': add `import nanyx/console`""",
         "Console",
         [ "Add `import nanyx/console`" ]),
        reported "def g: [Console] () -> () = { }\n")

[<Fact>]
let ``An unknown type says which import brings it in`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownType.Code,
         """Unknown type 'IoError'. 'IoError' is defined in 'nanyx/console': add `import nanyx/console`""",
         "IoError",
         [ "Add `import nanyx/console`" ]),
        reported "def r: IoError = #endOfInput\n")

[<Fact>]
let ``A name two modules export lists both, with an import for each`` () =
    let exports = Map.ofList [ "frobnicate", [ "nanyx/alpha"; "nanyx/beta" ] ]
    Assert.Equal(
        """. 'frobnicate' is defined in 'nanyx/alpha' and 'nanyx/beta': add `import nanyx/alpha` or `import nanyx/beta`""",
        StandardLibraryImports.hint exports "frobnicate")

[<Fact>]
let ``A name no standard-library module exports still asks about the names in scope`` () =
    let code, message, _, _ = reported "def one = 1\ndef two = onee\n"
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, code)
    Assert.Equal("Unknown identifier 'onee': did you mean 'one'?", message)
