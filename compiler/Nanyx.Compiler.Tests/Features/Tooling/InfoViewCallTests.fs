module Nanyx.Compiler.Tests.Features.Tooling.InfoViewCall

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The info view's "This call": the contexts the call at the cursor requires, and where it finds each.

/// The info view's call contexts at the `occurrence`th appearance of `text` in `source`, each as its context, where
/// the call finds it, and the text of what provides it.
let private callAt (source: string) (text: string) (occurrence: int) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let info = InfoView.at Ascii "" result (offsetOf source text occurrence 0)
    info.Call
    |> List.map (fun call ->
        let found =
            match call.Source with
            | InfoView.Provided InfoView.Signature -> "signature"
            | InfoView.Provided InfoView.Use -> "use"
            | InfoView.Provided InfoView.Inferred -> "inferred"
            | InfoView.Provided InfoView.Runtime -> "runtime"
            | InfoView.Attached definitions -> $"""attached {String.concat ", " definitions}"""
            | InfoView.Derived -> "derived"
            | InfoView.BuiltIn -> "built in"
        let provider = if call.Span.Start < 0 then "" else textAt source call.Span
        call.Context, found, provider)

let private showContext = """
context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def describe: [Show(a)] a -> string = { x -> show(x) }
"""

[<Fact>]
let ``A call's parameterised context from a use links to the value the use loads`` () =
    let source = showContext + """
def showPoint: Show(Point) = (show = { p -> "p" })

def main = {
  use showPoint
  describe(Point(x = 1, y = 2))
}
"""
    Assert.Equal<(string * string * string) list>(
        [ "Show(Point)", "use", "showPoint" ],
        callAt source "describe(Point" 0)

[<Fact>]
let ``A call's context from the signature links to the context the annotation writes`` () =
    let source = showContext + """
def twice: [Show(a)] a -> string = { x -> describe(x) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "Show(a)", "signature", "Show(a)" ],
        callAt source "describe(x)" 0)

[<Fact>]
let ``A call's instance built from attached definitions names them`` () =
    let source = showContext + """
def Point.show: Point -> string = { p -> "p" }

def main = { describe(Point(x = 1, y = 2)) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "Show(Point)", "attached Point.show", "" ],
        callAt source "describe(Point" 0)

[<Fact>]
let ``A call shows the contexts its callee's type declares, in order, provided by inference or the runtime`` () =
    let source = """import nanyx/console

def greet = { println("hi") }

println("top")
"""
    let toString = "ToString(string)", "attached string.toString", ""
    Assert.Equal<(string * string * string) list>([ "Console", "inferred", "greet"; toString ], callAt source "println" 0)
    Assert.Equal<(string * string * string) list>([ "Console", "runtime", ""; toString ], callAt source "println" 1)

[<Fact>]
let ``The innermost call that requires a context is shown`` () =
    let source = """
context Log = (log: string -> ())

def note: [Log] string -> string = { s ->
  log(s)
  s
}

def wrap: string -> string = { s -> s }

def main: [Log] () -> string = { wrap(note("a")) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "Log", "signature", "Log" ],
        callAt source "\"a\"" 0)
    Assert.Empty(callAt source "wrap(note" 0)
