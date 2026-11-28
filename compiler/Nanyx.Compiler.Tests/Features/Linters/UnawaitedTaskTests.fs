/// The official linter's `unawaitedTask` rule: a task thrown away though what it gives back isn't `()` (#1890).
module Nanyx.Compiler.Tests.Features.Linters.UnawaitedTask

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics of `source`, compiled with every linter on, with their codes and the text each is reported at. It
/// must have no compiler errors, or no linter would run.
let private reported (source: string) =
    let source = (withFinalNewline source).ReplaceLineEndings "\n"
    let result = Compiler.compileWith (everyLinter ()) source
    let compilerErrors = result.Diagnostics |> List.filter (fun diagnostic -> Diagnostics.isError diagnostic && not (diagnostic.Code.StartsWith "default/"))
    Assert.True(result.TypeChecked && compilerErrors.IsEmpty, sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unawaitedTask" || diagnostic.Code = "default/unusedResults")
    |> List.map (fun diagnostic ->
        let span = diagnostic.Span.Value
        diagnostic.Code, diagnostic.Severity, diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``A task thrown away though it gives back a value is a warning, and not an unused result`` () =
    let source =
        """def compute: [Async] int -> int = { n -> n * 2 }

def both = { _ ->
  start! compute(21)
  42
}"""
    match reported source with
    | [ code, severity, message, text ] ->
        Assert.Equal("default/unawaitedTask", code)
        Assert.Equal(Severity.Warning, severity)
        Assert.Equal("This task gives back a 'int', which is never read: await it, or start it for what it does, giving back `()`", message)
        Assert.Equal("start! compute(21)", text)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``A task started for what it does, giving back (), is neither`` () =
    let source =
        """def both = { _ ->
  start! println("logged")
  42
}"""
    Assert.Empty(reported source)

[<Fact>]
let ``An awaited task is neither`` () =
    let source =
        """def both = { _ ->
  def task = start! 21
  await(task) * 2
}"""
    Assert.Empty(reported source)
