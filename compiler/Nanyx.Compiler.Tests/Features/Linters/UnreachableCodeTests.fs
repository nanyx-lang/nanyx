module Nanyx.Compiler.Tests.Features.Linters.UnreachableCode

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/unreachableCode` reports for `source`, compiled with every linter on: each diagnostic with the text
/// it is reported at. The module must type-check, or no linter would run.
let private diagnostics (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unreachableCode")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

let private reported (source: string) =
    diagnostics source |> List.map (fun (diagnostic, text) -> diagnostic.Message, text)

[<Fact>]
let ``The statements after a call to panic are reported, through the end of the block`` () =
    let source =
        "def f: int -> int = { x ->\n"
        + "  panic(\"bad input\")\n"
        + "  def y = x + 1\n"
        + "  y * 2\n"
        + "}\n"
    Assert.Equal<(string * string) list>(
        [ "This code never runs: `panic` never returns", "def y = x + 1\n  y * 2" ],
        reported source)

[<Fact>]
let ``The diverging statement is related, and the fix removes what follows it`` () =
    let source =
        "def f: int -> int = { x ->\n"
        + "  panic(\"bad input\")\n"
        + "  x + 1 -- never\n"
        + "}\n"
    match diagnostics source with
    | [ diagnostic, _ ] ->
        let related = diagnostic.Related |> List.map (fun (span, message) -> source.Substring(span.Start, span.End - span.Start), message)
        Assert.Equal<(string * string) list>([ """panic("bad input")""", "never gives a value" ], related)
        Assert.Equal("def f: int -> int = { x ->\n  panic(\"bad input\")\n}\n", applyFix source (List.exactlyOne diagnostic.Fixes))
    | other -> Assert.Fail($"Expected one report, got %A{other}")

[<Fact>]
let ``A call to any function whose result is undefined counts, as does a def bound to one`` () =
    let source =
        "def fail: string -> undefined = { message -> panic(\"failed: {message}\") }\n"
        + "def f: int -> int = { x ->\n"
        + "  def v = fail(\"x\")\n"
        + "  x\n"
        + "}\n"
    match diagnostics source with
    | [ diagnostic, text ] ->
        Assert.Equal("This code never runs: `fail` never returns", diagnostic.Message)
        Assert.Equal("x", text)
        // Removing `x` would leave the block ending with a def.
        Assert.Empty(diagnostic.Fixes)
    | other -> Assert.Fail($"Expected one report, got %A{other}")

[<Fact>]
let ``An if whose every branch never returns is a statement that never returns`` () =
    let source =
        "def f: bool -> int = { b ->\n"
        + "  if b then panic(\"a\") else panic(\"b\")\n"
        + "  1\n"
        + "}\n"
    Assert.Equal<(string * string) list>([ "This code never runs: the statement before it never gives a value", "1" ], reported source)

[<Fact>]
let ``A panic as a block's last statement, or in only one branch, is not reported`` () =
    Assert.Empty(reported "def f: int -> int = { x ->\n  def y = x + 1\n  panic(\"no {y}\")\n}\n")
    Assert.Empty(reported "def f: int -> int = { x ->\n  def y = if x > 0 then x else panic(\"no\")\n  y + 1\n}\n")
