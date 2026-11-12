module Nanyx.Compiler.Tests.Features.Linters.PlusBetweenContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/plusBetweenContexts` reports for `source`, compiled with every linter on: each diagnostic with the
/// text it is reported at. The module must type-check, or no linter would run.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/plusBetweenContexts")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``A + between a signature's contexts is reported, with a fix that writes &`` () =
    let source =
        """def both: [Eq(a) + Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) }
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal("+", text)
        Assert.Equal(
            """def both: [Eq(a) & Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) }
""".ReplaceLineEndings "\n",
            applyFix (source.ReplaceLineEndings "\n") diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``Each + is reported, in a local def's annotation and a context member's requirement too`` () =
    let source =
        """def f: [Eq(a) + Hash(a) + `+`: a, a -> a] a -> a = { x -> x + x }

def g = {
  def h: [Eq(b) + Hash(b)] b -> bool = { y -> y == y and hash(y) == hash(y) }
  h(1)
}
"""
    Assert.Equal<string list>([ "+"; "+"; "+" ], reported source |> List.map snd)

[<Fact>]
let ``Contexts joined with & are not reported`` () =
    let source =
        """def both: [Eq(a) & Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) }
"""
    Assert.Empty(reported source)
