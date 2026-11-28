/// A tag whose payload is `undefined` has no values (#3129): a match needs no arm for it, and an arm for it checks, as code
/// after a `crash` does, though it can never be chosen (see the official linter's `undefinedPayloadArm`).
module Nanyx.Compiler.Tests.Features.Types.UndefinedPayload

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A match on the head of a list of undefined is complete with an arm for #emptyList alone`` () =
    Assert.Empty((Compiler.compile "def items = []\n\ndef n = match items \\head\n  | #emptyList -> 0\n").Diagnostics)

[<Fact>]
let ``An arm for #some on the head of a list of undefined checks`` () =
    Assert.Empty((Compiler.compile "def items = []\n\ndef n = match items \\head\n  | #emptyList -> 0\n  | #some(x) -> 1\n").Diagnostics)

[<Fact>]
let ``An arm for a tag the value has nowhere is still an error`` () =
    let result = Compiler.compile "def items = []\n\ndef n = match items \\head\n  | #none -> 0\n  | #emptyList -> 1\n"
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)

[<Fact>]
let ``The head of a list of undefined in a function is #emptyList`` () =
    let typed = compileClean (normalized "def first = { ->\n  def local = []\n  local \\head\n}\n")
    Assert.Equal("() -> #emptyList", asciiTypeIn typed "first")
