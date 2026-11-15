module Nanyx.Compiler.Tests.Features.ScopedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `scoped context`: nothing that requires the context can leave the scope that supplies it. A scoped context must be
// either protected or private, so that only its own module makes its instances.

[<Fact>]
let ``A scoped context that is neither protected nor private is an error at scoped`` () =
    let result = Compiler.compile "scoped context Tx = (commit: () -> ())\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.ScopedContextNotProtected.Code, diagnostic.Code)
    Assert.Equal(
        "The scoped context 'Tx' must be either protected or private, as in `scoped context Tx = protected (...)`, so that only its own module can make its instances",
        diagnostic.Message)

[<Fact>]
let ``A local scoped context that is neither protected nor private is an error at scoped`` () =
    let source = """def main = {
  scoped context Tx = ()
  1
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ScopedContextNotProtected.Code, code)
    Assert.Equal("scoped", text)

[<Fact>]
let ``Any module may declare a scoped context, if it is protected`` () =
    let typed = compileClean "scoped export context Transaction = protected (commit: () -> ())\n"
    let info = typed.TypeDefs |> Map.find "Transaction"
    Assert.True(info.IsContext)
    Assert.True(info.IsScoped)

[<Fact>]
let ``A scoped context may be private`` () =
    let typed = compileClean "scoped export context Transaction = private (commit: () -> ())\n"
    let info = typed.TypeDefs |> Map.find "Transaction"
    Assert.True(info.IsScoped)
    Assert.True(info.IsPrivate)

[<Fact>]
let ``The prelude's Memory is scoped`` () =
    let typed = compileClean "def main = 1\n"
    Assert.True((typed.TypeDefs |> Map.find "Memory").IsScoped)

[<Fact>]
let ``Hovering a scoped context's name shows that it is scoped`` () =
    let source = "scoped context Tx = protected ()\ndef run: [Tx] () -> () = { () }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let hover = Definition.typeDefinitionAt Unicode "" result (offsetOf source "Tx]" 0 1)
    Assert.Equal(Some "scoped context Tx = protected ()", hover |> Option.map _.Type)

[<Fact>]
let ``The outline tells a scoped context from another`` () =
    let outline = (Outline.ofSource "" "scoped context Tx = protected ()\ncontext Log = ()\n").Value
    Assert.Equal<Outline.OutlineKind list>(
        [ Outline.OutlineKind.ScopedContextType; Outline.OutlineKind.ContextType ],
        outline |> List.map _.Kind)
