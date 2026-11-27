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
let ``The statements after a call to crash are reported, through the end of the block`` () =
    let source =
        "def f: int -> int = { x ->\n"
        + "  crash(\"bad input\")\n"
        + "  def y = x + 1\n"
        + "  y * 2\n"
        + "}\n"
    Assert.Equal<(string * string) list>(
        [ "This code never runs: `crash` never returns", "def y = x + 1\n  y * 2" ],
        reported source)

[<Fact>]
let ``The diverging statement is related, and the fix removes what follows it`` () =
    let source =
        "def f: int -> int = { x ->\n"
        + "  crash(\"bad input\")\n"
        + "  x + 1 -- never\n"
        + "}\n"
    match diagnostics source with
    | [ diagnostic, _ ] ->
        let related = diagnostic.Related |> List.map (fun (span, message) -> source.Substring(span.Start, span.End - span.Start), message)
        Assert.Equal<(string * string) list>([ """crash("bad input")""", "never gives a value" ], related)
        Assert.Equal("def f: int -> int = { x ->\n  crash(\"bad input\")\n}\n", applyFix source (List.exactlyOne diagnostic.Fixes))
    | other -> Assert.Fail($"Expected one report, got %A{other}")

[<Fact>]
let ``A call to any function whose result is undefined counts, as does a def bound to one`` () =
    let source =
        (withFinalNewline """def fail: string -> undefined = { message -> crash("failed: {message}") }""")
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
        + "  if b then crash(\"a\") else crash(\"b\")\n"
        + "  1\n"
        + "}\n"
    Assert.Equal<(string * string) list>([ "This code never runs: the statement before it never gives a value", "1" ], reported source)

[<Fact>]
let ``The arguments after one that never returns are reported, and the call is never made`` () =
    let source =
        withFinalNewline
            """def g: () -> int = { -> 2 }
def f: int, int, int -> int = { a, b, c -> a + b + c }
def h: () -> int = { -> f(1, crash("a"), g()) }"""
    match diagnostics source with
    | [ diagnostic, text ] ->
        Assert.Equal("This code never runs: `crash` never returns, so the arguments after it are never evaluated and `f` is never called", diagnostic.Message)
        Assert.Equal("g()", text)
        let related = diagnostic.Related |> List.map (fun (span, message) -> source.Substring(span.Start, span.End - span.Start), message)
        Assert.Equal<(string * string) list>([ """crash("a")""", "never gives a value" ], related)
        Assert.Empty(diagnostic.Fixes)
    | other -> Assert.Fail($"Expected one report, got %A{other}")

[<Fact>]
let ``A piped value that never returns makes the pipe's arguments unreachable`` () =
    let source =
        withFinalNewline
            """def f: int, int, int -> int = { a, b, c -> a + b + c }
def h: () -> int = { -> crash("a") \f(1, 2) }"""
    Assert.Equal<string list>([ "1, 2" ], reported source |> List.map snd)

[<Fact>]
let ``A last argument that never returns leaves nothing after it to report`` () =
    let source =
        withFinalNewline
            """def f: int, int -> int = { a, b -> a + b }
def h: () -> int = { -> f(1, crash("a")) }"""
    Assert.Empty(reported source)

[<Fact>]
let ``A crash as a block's last statement, or in only one branch, is not reported`` () =
    Assert.Empty(reported (withFinalNewline """def f: int -> int = { x ->
  def y = x + 1
  crash("no {y}")
}"""))
    Assert.Empty(reported (withFinalNewline """def f: int -> int = { x ->
  def y = if x > 0 then x else crash("no")
  y + 1
}"""))

[<Fact>]
let ``The statements after a return in a workflow's block are reported, and the fix removes them`` () =
    let source =
        "def f: int -> #some(int) | #none = { n ->\n"
        + "  proc {\n"
        + "    return n\n"
        + "    println(\"after\")\n"
        + "  }\n"
        + "}\n"
    match diagnostics source with
    | [ diagnostic, text ] ->
        Assert.Equal("This code never runs: `return` ends the block", diagnostic.Message)
        Assert.Equal("println(\"after\")", text)
        let related = diagnostic.Related |> List.map (fun (span, message) -> source.Substring(span.Start, span.End - span.Start), message)
        Assert.Equal<(string * string) list>([ "return n", "ends the block" ], related)
        Assert.Equal("def f: int -> #some(int) | #none = { n ->\n  proc {\n    return n\n  }\n}\n", applyFix source (List.exactlyOne diagnostic.Fixes))
    | other -> Assert.Fail($"Expected one report, got %A{other}")

[<Fact>]
let ``A return in a branch does not make what follows the branch unreachable`` () =
    let source =
        "def f: int -> #some(int) | #none = { n ->\n"
        + "  proc {\n"
        + "    def m = if n > 2 then return n else n * 2\n"
        + "    return m\n"
        + "  }\n"
        + "}\n"
    Assert.Empty(reported source)

[<Fact>]
let ``The statements after a call to a function inferred never to return are reported`` () =
    // An unannotated function whose result is a variable nothing else has is inferred to give back undefined (#1864).
    let source =
        "def loop = { x -> loop(x) }\n"
        + "def f: int -> int = { x ->\n"
        + "  loop(x)\n"
        + "  x + 1\n"
        + "}\n"
    Assert.Equal<string list>([ "x + 1" ], diagnostics source |> List.map snd)
