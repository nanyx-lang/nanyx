module Nanyx.Compiler.Tests.Features.Linters.UseInBraces

open Xunit
open NyxCompiler

/// What `default/useInBraces` reports for `source`, compiled with every linter on: each message with the text it is
/// reported at. The module must type-check, or no linter would run. Its line endings are made `\n`, as for spans.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/useInBraces")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

let private counter =
    """context Counter = (next: int -> int)
def counted: [Counter] int -> int = { n -> next(n) }
def down: Counter = (next = { n -> n - 1 })
"""

[<Fact>]
let ``use ... in with a body in braces is reported (#673)`` () =
    Assert.Equal<(string * string) list>(
        [ "The braces make this a function of no arguments, not a block run with the context loaded: put the statements on the lines after `in`, indented",
          "{ counted(3) }" ],
        reported (counter + "def f = use down in { counted(3) }"))

[<Fact>]
let ``use ... in with an indented block, or a lambda written with its arguments, is not reported`` () =
    Assert.Empty(
        reported (
            counter
            + """def f = {
  use down in
    def a = counted(3)
    a + 1
}
"""
        )
    )
    Assert.Empty(reported (counter + "def f = use down in { n -> counted(n) }"))
    Assert.Empty(reported (counter + "def f = use down in { () -> counted(3) }"))