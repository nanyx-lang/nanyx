module Nanyx.Compiler.Tests.Features.Linters.Semicolons

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/semicolons` reports for `source`, compiled with every linter on: each diagnostic with the text it is
/// reported at. The module must type-check, or no linter would run. Its line endings are made `\n`, as for spans.
let private reported (source: string) =
    let source = normalized source
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/semicolons")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``Each semicolon between a block's statements is reported, with a fix that puts the next statement on its own line`` () =
    let source =
        normalized """def f = { x ->
  def y = x + 1; def z = y * 2
  z
}
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal(";", text)
        Assert.Equal("Put each statement on a line of its own, rather than separating them with `;`", diagnostic.Message)
        let expected =
            """def f = { x ->
  def y = x + 1
  def z = y * 2
  z
}
"""
        Assert.Equal(normalized expected, applyFix source diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A semicolon in a one line body is reported without a fix`` () =
    match reported "def f = { x -> def y = x + 1; y * 2 }\n" with
    | [ diagnostic, ";" ] -> Assert.Empty(diagnostic.Fixes)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``Statements on lines of their own are not reported`` () =
    Assert.Empty(
        reported
            """def f = { x ->
  def y = x + 1
  y * 2
}
""")
