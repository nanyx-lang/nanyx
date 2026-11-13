module Nanyx.Compiler.Tests.Features.ScopedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `scoped context`: nothing that requires the context can leave the scope that supplies it. A scoped context must be
// nominal, so that only its own module makes its instances.

[<Fact>]
let ``A scoped context that isn't nominal is an error at scoped`` () =
    let code, text = singleError "scoped context Tx = (commit: () -> ())\n"
    Assert.Equal(DiagnosticCodes.ScopedContextNotNominal.Code, code)
    Assert.Equal("scoped", text)

[<Fact>]
let ``A local scoped context that isn't nominal is an error at scoped`` () =
    let source = """def main = {
  scoped context Tx = ()
  1
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ScopedContextNotNominal.Code, code)
    Assert.Equal("scoped", text)

[<Fact>]
let ``Any module may declare a scoped context, if it is nominal`` () =
    let typed = compileClean "scoped export context $Transaction = (commit: () -> ())\n"
    let info = typed.TypeDefs |> Map.find "$Transaction"
    Assert.True(info.IsContext)
    Assert.True(info.IsScoped)

[<Fact>]
let ``The prelude's $Memory is scoped`` () =
    let typed = compileClean "def main = 1\n"
    Assert.True((typed.TypeDefs |> Map.find "$Memory").IsScoped)

[<Fact>]
let ``Hovering a scoped context's name shows that it is scoped`` () =
    let source = "scoped context $Tx = ()\ndef run: [$Tx] () -> () = { () }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let hover = Definition.typeDefinitionAt Unicode "" result (offsetOf source "$Tx]" 0 1)
    Assert.Equal(Some "scoped context $Tx = ()", hover |> Option.map _.Type)

[<Fact>]
let ``The outline tells a scoped context from another`` () =
    let outline = (Outline.ofSource "" "scoped context $Tx = ()\ncontext Log = ()\n").Value
    Assert.Equal<Outline.OutlineKind list>(
        [ Outline.OutlineKind.ScopedContextType; Outline.OutlineKind.ContextType ],
        outline |> List.map _.Kind)
