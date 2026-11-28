module Nanyx.Compiler.Tests.Features.Linters.UnusedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The `default/unusedContexts` diagnostics for `source`, compiled with every linter on. The module must type-check, or
/// no linter would run.
let private unusedContexts (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith (everyLinter ()) source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedContexts")

/// What `default/unusedContexts` reports for `source`: each message with the text it is reported at.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    unusedContexts source
    |> List.map (fun diagnostic ->
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

/// `source` with the only fix of its only `unusedContexts` diagnostic applied, and the fix's title.
let private fixed' (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let diagnostic = Assert.Single(unusedContexts source)
    let fix = Assert.Single(diagnostic.Fixes)
    applyFix source fix, fix.Title

let private log = "context Log = (log: string -> ())\n"

[<Fact>]
let ``A scoped context the body doesn't use is reported, as unnecessary, with a fix that removes the brackets`` () =
    let source = "scoped context Foo = protected ()\n\ndef f: [Foo] () -> () = { }\n"
    let diagnostic = Assert.Single(unusedContexts source)
    Assert.Equal(Severity.Warning, diagnostic.Severity)
    Assert.True(diagnostic.Unnecessary)
    Assert.Equal<(string * string) list>([ "'Foo' is required but not used by 'f'", "Foo" ], reported source)
    Assert.Equal(("scoped context Foo = protected ()\n\ndef f: () -> () = { }\n", "Remove `Foo`"), fixed' source)

[<Fact>]
let ``A context whose member the body calls is used`` () =
    Assert.Empty(reported (log + "def f: [Log] string -> () = { s -> log(s) }\n"))

[<Fact>]
let ``A context the body passes on to a call that requires it is used`` () =
    Assert.Empty(
        reported (
            log
            + "export def f: [Log] string -> () = { s -> log(s) }\ndef g: [Log] () -> () = { () -> f(\"x\") }\n"
        )
    )

[<Fact>]
let ``A scoped context passed on to a call is used, and the def that uses nothing is reported`` () =
    let source =
        """scoped context Foo = protected ()

def a: [Foo] () -> () = { () -> () }
def b: [Foo] () -> () = { () -> a() }
"""
    Assert.Equal<(string * string) list>([ "'Foo' is required but not used by 'a'", "Foo" ], reported source)

[<Fact>]
let ``A context an operator reads is used`` () =
    Assert.Empty(reported "def add: [Add(a)] a, a -> a = { x, y -> x + y }\n")

[<Fact>]
let ``An unused context among others is removed with its comma`` () =
    let source =
        log
        + "context Clock = (now: () -> int)\ndef f: [Log, Clock] () -> int = { () -> now() }\ndef g: [Clock, Log] () -> int = { () -> now() }\n"
    Assert.Equal<(string * string) list>(
        [ "'Log' is required but not used by 'f'", "Log"; "'Log' is required but not used by 'g'", "Log" ],
        reported source)
    let diagnostics = unusedContexts source
    let applied = diagnostics |> List.map (fun diagnostic -> applyFix (source.ReplaceLineEndings "\n") (Assert.Single diagnostic.Fixes))
    Assert.Contains("def f: [Clock] () -> int", applied.[0])
    Assert.Contains("def g: [Clock] () -> int", applied.[1])

[<Fact>]
let ``An exported def is not reported`` () =
    Assert.Empty(reported "scoped context Foo = protected ()\n\nexport def f: [Foo] () -> () = { }\n")

[<Fact>]
let ``A context variable is not a context`` () =
    Assert.Empty(reported "def call: [c] ([c] () -> ()) -> () = { f -> f() }\n")

[<Fact>]
let ``A local def's unused context is reported`` () =
    let source =
        log
        + """def main = {
  def say: [Log] () -> () = { () -> () }
  1
}
"""
    Assert.Equal<(string * string) list>([ "'Log' is required but not used by 'say'", "Log" ], reported source)
