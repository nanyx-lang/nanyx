module Nanyx.Compiler.Tests.Features.Linters.UndefinedPayloadArm

open Xunit
open NyxCompiler

// An arm for a tag whose payload is `undefined`, which no value has, checks, and is drawn faint with a fix that removes
// it (#3129).

/// What `default/undefinedPayloadArm` reports for `source`, compiled with every linter on: each message with the text
/// it is reported at, and the text its fix gives. The module must type-check, or no linter would run.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/undefinedPayloadArm")
    |> List.map (fun diagnostic ->
        Assert.Equal((Severity.Hint, true), (diagnostic.Severity, diagnostic.Unnecessary))
        let span = diagnostic.Span.Value
        let fix = Assert.Single diagnostic.Fixes
        let edit = Assert.Single fix.Edits
        let fixedText = source.Substring(0, edit.Span.Start) + edit.NewText + source.Substring edit.Span.End
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start), fixedText)

[<Fact>]
let ``An arm for #some on the head of a list of undefined is reported, and its fix removes it`` () =
    let source = "def items = []\n\ndef n = match items \\head\n  | #emptyList -> 0\n  | #some(x) -> 1\n"
    Assert.Equal<(string * string * string) list>(
        [ "This arm can never match: `#some` would hold a value of type `undefined`, of which there is none",
          "#some(x) -> 1",
          "def items = []\n\ndef n = match items \\head\n  | #emptyList -> 0\n" ],
        reported source)

[<Fact>]
let ``An arm for a tag the value can be is not reported`` () =
    Assert.Empty(reported "def xs: list(int) = [1]\n\ndef n = match xs \\head\n  | #emptyList -> 0\n  | #some(x) -> x\n")
