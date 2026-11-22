/// An exported scoped context is written `export scoped context`; the order of earlier versions, `scoped export
/// context`, is an error with a fix (#2100).
module Nanyx.Compiler.Tests.Features.KeywordOrder

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic `source` compiles to: its code, its message, the text it is at, and the title of each fix with
/// the source it gives.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        let span = diagnostic.Span.Value
        diagnostic.Code, diagnostic.Message, source.Substring(span.Start, span.End - span.Start), diagnostic.Fixes |> List.map (fun fix -> fix.Title, applyFix source fix)
    | diagnostics -> failwith $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``export scoped context declares an exported scoped context`` () =
    let typed = compileClean "export scoped context Tx = protected ()\n"
    let info = typed.TypeDefs |> Map.find "Tx"
    Assert.True(info.IsScoped)

[<Fact>]
let ``scoped export context is an error with a fix that writes export scoped`` () =
    Assert.Equal(
        (DiagnosticCodes.KeywordsOutOfOrder.Code,
         "Write `export` before `scoped`: `export scoped context`",
         "scoped export",
         [ "Write `export scoped`", "export scoped context Tx = protected ()\n" ]),
        reported "scoped export context Tx = protected ()\n")

[<Fact>]
let ``scoped export context in a block is an error too`` () =
    let source =
        """def main = {
  scoped  export context Tx = protected ()
  1
}
"""
    let code, _, text, fixes = reported source
    Assert.Equal(DiagnosticCodes.KeywordsOutOfOrder.Code, code)
    Assert.Equal("scoped  export", text)
    Assert.Equal<string list>([ "def main = {\n  export scoped context Tx = protected ()\n  1\n}\n" ], fixes |> List.map snd)

[<Fact>]
let ``scoped context and export context are not reported`` () =
    compileClean "scoped context Tx = protected ()\nexport context Show(a) = (show: a -> string)\n" |> ignore
