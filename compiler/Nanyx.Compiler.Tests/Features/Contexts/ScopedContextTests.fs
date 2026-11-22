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
    let typed = compileClean "export scoped context Transaction = protected (commit: () -> ())\n"
    let info = typed.TypeDefs |> Map.find "Transaction"
    Assert.True(info.IsContext)
    Assert.True(info.IsScoped)

[<Fact>]
let ``A scoped context may be private`` () =
    let typed = compileClean "export scoped context Transaction = private (commit: () -> ())\n"
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
let ``Calls in one block that require a scoped context require the block's, once`` () =
    let typed = compileClean ("""
scoped context Tx = private ()

def transaction: [c] ([Tx, c] () -> a) -> a = { block -> use Tx() in block() }
def step: [Tx] int -> int = { n -> n + 1 }

def twice: [Console] () -> () = { println(transaction { step(step(1)) }) }
def thrice = transaction { step(1) + step(2) + step(3) }
""".ReplaceLineEndings "\n")
    Assert.Equal("int", Unifier.tyToString typed.Types.["thrice"])

[<Fact>]
let ``The outline tells a scoped context from another`` () =
    let outline = (Outline.ofSource "" "scoped context Tx = protected ()\ncontext Log = ()\n").Value
    Assert.Equal<Outline.OutlineKind list>(
        [ Outline.OutlineKind.ScopedContextType; Outline.OutlineKind.ContextType ],
        outline |> List.map _.Kind)

// A type that requires a scoped context at more than one scope numbers them in order of first appearance; at one
// scope it is written plain (#1553).

let private twoScopes = """def run2 = { f, g ->
  def a = memory { f() }
  memory { g() }
}

def one = { f -> memory { f() } }

def nested = memory {
  mut y = 0
  memory {
    mut z = 1
    def both = { y := z }
    both()
  }
  y
}
"""

[<Fact>]
let ``A scoped context required at two scopes is numbered in the module's types and in hover`` () =
    let source = twoScopes.ReplaceLineEndings "\n"
    let typed = compileClean source
    Assert.Equal("([Memory₁] () -> a), ([Memory₂] () -> b) -> b", Unifier.tyToDisplayString Ascii typed.Types.["run2"])
    Assert.Equal("([Memory] () -> a) -> a", Unifier.tyToDisplayString Ascii typed.Types.["one"])
    Assert.Equal(Some "both: [Memory₁, Memory₂] () -> ()", hoverType source "both =")

[<Fact>]
let ``So it is in inlay hints and in error messages`` () =
    let source = twoScopes.ReplaceLineEndings "\n"
    let hints = localInlayHints Unicode "" (Compiler.compile source) |> List.map (fun hint -> hint.Name, hint.Type)
    Assert.Contains(("both", "[Memory₁, Memory₂] () -> ()"), hints)
    let result = Compiler.compile (source + "def wrong: int = run2\n")
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Contains("Memory₁", diagnostic.Message)
    Assert.Contains("Memory₂", diagnostic.Message)
