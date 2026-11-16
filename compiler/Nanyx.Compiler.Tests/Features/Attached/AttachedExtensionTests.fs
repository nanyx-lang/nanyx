module Nanyx.Compiler.Tests.Features.AttachedExtensions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The code of each diagnostic compiling `source` gives, with the text it is reported at.
let private reported (source: string) =
    let source = normalized source
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``A new attached definition extends a built-in type from any module`` () =
    let typed = compileClean (withFinalNewline """def string.shout: string -> string = { s -> s ++ "!" }
def loud = "hi" \shout""")
    Assert.Equal("string", Unifier.tyToString typed.Types.["loud"])

[<Theory>]
[<InlineData("def string.length: string -> int = { s -> 42 }", "string.length", "from nanyx/text")>]
[<InlineData("def list.map = { xs, f -> [] }", "list.map", "from the prelude")>]
[<InlineData("def string.hash: string -> int = { s -> 0 }", "string.hash", "from the prelude")>]
[<InlineData("def int.toString: int -> string = { n -> \"lol\" }", "int.toString", "from the prelude")>]
let ``An attached definition the standard library already has for a built-in type is an error`` (source: string, name: string, from: string) =
    let diagnostic = Assert.Single((Compiler.compile (source + "\n")).Diagnostics)
    Assert.Equal(DiagnosticCodes.AttachedDefinitionInScope.Code, diagnostic.Code)
    Assert.Equal(name, textAt (source + "\n") (spanOf diagnostic))
    Assert.Contains(from, diagnostic.Message)

[<Fact>]
let ``A comparison attached to a built-in type is an error, as its order is the compiler's own`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnattachableDefinition.Code, "int.`<`" ],
        reported "def int.`<`: int, int -> bool = { a, b -> true }\n")

[<Fact>]
let ``An attached definition an import brings in for its type is an error, naming where it is defined`` () =
    let geo =
        normalized """module geo

export type Point = protected (x: int, y: int)
export def Point.norm: Point -> int = { p -> p.x + p.y }
"""
    let main =
        normalized """import geo

def Point.norm: Point -> int = { p -> 0 }
def Point.twice: Point -> int = { p -> Point.norm(p) * 2 }
"""
    let result = compileImportingAll [ "geo.nyx", geo ] main
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.AttachedDefinitionInScope.Code, diagnostic.Code)
    Assert.Equal("Point.norm", textAt main (spanOf diagnostic))
    let related, _ = Assert.Single(diagnostic.Related)
    Assert.EndsWith("geo.nyx", related.File)
