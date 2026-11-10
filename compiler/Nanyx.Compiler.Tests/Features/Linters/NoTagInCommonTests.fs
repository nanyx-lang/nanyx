module Nanyx.Compiler.Tests.Features.Linters.NoTagInCommon

open Xunit
open NyxCompiler

/// What `default/noTagInCommon` reports for `source`, compiled with every linter on: each message with the text it is
/// reported at. The module must type-check, or no linter would run.
let private reported (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/noTagInCommon")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``Comparing closed tag types with no tag in common is reported`` () =
    Assert.Equal<(string * string) list>(
        [ "'#red' and '#some(int)' have no tag in common, so this is always false", "#red == #some(1)" ],
        reported "def b = #red == #some(1)\n")
    Assert.Equal<(string * string) list>(
        [ "'#red | #green' and '#blue' have no tag in common, so this is always true", "c != #blue" ],
        reported "def b: #red | #green -> bool = { c -> c != #blue }\n")

[<Fact>]
let ``Comparing types that share a tag, or an open union, is not reported`` () =
    Assert.Empty(reported "def b: #red | #green -> bool = { c -> c == #red }\n")
    Assert.Empty(reported "def isNone = { o -> o == #none }\ndef b = isNone(#some(3))\n")
    Assert.Empty(reported "def b = 1 == 2\n")
