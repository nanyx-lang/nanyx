module Nanyx.Compiler.Tests.Features.InferredContextVariableNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context variable the compiler infers for an unannotated def has a name no annotation can write, `'1`, which a reader
// never sees: it is written `κ`, `λ`, ... in Unicode and `c`, `d`, ... in ASCII and source, as made-up type variables are
// written `α` and `a`.

let private hoverIn (notation: TypeNotation) (source: string) (text: string) =
    let source = normalized source
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Hover.typeAt notation result (source.IndexOf text) |> Option.map _.Type

[<Fact>]
let ``An inferred context variable is Greek in Unicode and Latin in ASCII`` () =
    let source =
        """
def apply = { f, x -> f(x) }
"""
    Assert.Equal(Some "def apply: [κ] ([κ] α -> β), α -> β", hoverIn Unicode source "apply =")
    Assert.Equal(Some "def apply: [c] ([c] a -> b), a -> b", hoverIn Ascii source "apply =")

[<Fact>]
let ``A def's second inferred context variable is λ`` () =
    let source =
        """
def both = { f, g, x -> (f(x), g(x)) }
"""
    Assert.Equal(Some "def both: [κ, λ] ([κ] α -> β), ([λ] α -> γ), α -> (β, γ)", hoverIn Unicode source "both =")
    Assert.Equal(Some "def both: [c, d] ([c] a -> b), ([d] a -> e), a -> (b, e)", hoverIn Ascii source "both =")

[<Fact>]
let ``An inferred context variable skips a letter the type already uses`` () =
    let source =
        """
def withK = { f, k: list(κ) -> (f(1), k) }
"""
    Assert.Equal(Some "def withK: [λ] ([λ] int -> α), list(κ) -> (α, list(κ))", hoverIn Unicode source "withK =")

[<Fact>]
let ``A context variable written κ in an annotation keeps its name`` () =
    let source =
        """
def run: [κ] ([κ] int -> int) -> int = { f -> f(1) }

def apply = { f, x -> f(x) }
"""
    Assert.Equal(Some "def run: [κ] ([κ] int -> int) -> int", hoverIn Unicode source "run:")
    Assert.Equal(Some "def apply: [κ] ([κ] α -> β), α -> β", hoverIn Unicode source "apply =")

[<Fact>]
let ``An inlay hint shows κ and writes c as an annotation`` () =
    let source = normalized "def apply = { f, x -> f(x) }\n"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let hint = TypeHints.inlayHintsWithTopLevel Unicode "" result |> List.find (fun hint -> hint.Name = "apply")
    Assert.Equal("[κ] ([κ] α -> β), α -> β", hint.Type)
    Assert.Equal(Some "[c] ([c] a -> b), a -> b", hint.Annotation)

[<Fact>]
let ``An imported def's inferred context variable is κ`` () =
    let library =
        """module mod
export def apply = { f, x -> f(x) }
"""
    let main =
        normalized
            """module main
import mod
def twice = { n -> apply({ m -> m * 2 }, n) }
"""
    let result = compileImporting (normalized library) main
    Assert.Empty(result.Diagnostics)
    let info = InfoView.at Unicode "" result (main.IndexOf "apply")
    let apply = info.Scopes |> List.last |> _.Names |> List.find (fun name -> name.Name = "apply")
    Assert.Equal(Some "[κ] ([κ] α -> β), α -> β", apply.Type)

[<Fact>]
let ``No inferred context variable's own name reaches what a reader sees`` () =
    let source =
        normalized
            """
def apply = { f, x -> f(x) }

def both = { f, g, x -> (f(x), g(x)) }

def run = { n -> apply({ m -> m + 1 }, n) }
"""
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let typed = result.Typed.Value
    for KeyValue(_, ty) in typed.Types do
        for notation in [ Unicode; Ascii; Source ] do
            Assert.DoesNotContain("'", Unifier.tyToDisplayString notation ty)
        Assert.DoesNotContain("'", Unifier.tyToString ty)
    for offset in 0 .. source.Length - 1 do
        match Hover.typeAt Unicode result offset with
        | Some hover -> Assert.DoesNotContain("'", hover.Type)
        | None -> ()
        for scope in (InfoView.at Unicode "" result offset).Scopes do
            for name in scope.Names do
                Assert.DoesNotContain("'", name.Type |> Option.defaultValue "")
    for hint in TypeHints.inlayHintsWithTopLevel Unicode "" result do
        Assert.DoesNotContain("'", hint.Type)
        Assert.DoesNotContain("'", hint.Annotation |> Option.defaultValue "")