module Nanyx.Compiler.Tests.Features.Linters.StrayDocComments

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/strayDocComments` reports for `source`, compiled with every linter on: each diagnostic with the text
/// it is reported at. The module must type-check, or no linter would run. Its line endings are made `\n`, as for spans.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/strayDocComments")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``A doc comment that documents nothing is reported, with a fix that makes it an ordinary comment`` () =
    let source =
        """def f = {
  --| The total.
    Of everything.
  42
}
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal("--| The total.\n    Of everything.", text)
        Assert.Equal(
            "This doc comment documents nothing: put it on the line before a definition, a type, a field or a tag, or make it an ordinary comment with `--`",
            diagnostic.Message)
        Assert.Equal(
            """def f = {
  -- The total.
    -- Of everything.
  42
}
""",
            applyFix (source.ReplaceLineEndings "\n") diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A doc comment that documents something is not reported`` () =
    let source =
        """--| The module.
import nanyx/text

--| A point.
type Point = (
  --| Across.
  x: int
)

--| Things that count.
context Counter = (
  --| The next number.
  next: int -> int
)

--| One.
def one = {
  --| Also one.
  def two = 1
  two
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A doc comment at the end of the file is reported`` () =
    Assert.Equal<string list>([ "--| Nothing after it." ], reported "def a = 1\n--| Nothing after it.\n" |> List.map snd)
