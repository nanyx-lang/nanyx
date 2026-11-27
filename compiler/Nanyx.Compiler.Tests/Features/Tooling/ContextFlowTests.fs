module Nanyx.Compiler.Tests.Features.Tooling.ContextFlow

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The info view's flow view: each provider of contexts in a def, an instance the compiler builds among them, with the
// calls it serves, and the highlights between a provider and its calls.

let private compiled (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result

/// The flow of the def around the `occurrence`th appearance of `text` in `source`: the def's name, and each provider as
/// its context, its source, the text of its span (none for an instance the compiler builds), and the names of the calls
/// it serves.
let private flowAt (source: string) (text: string) (occurrence: int) =
    (ContextFlow.at Ascii (compiled source) (offsetOf source text occurrence 0)).Flow
    |> Option.map (fun flow ->
        flow.Def,
        flow.Entries
        |> List.map (fun entry ->
            let found =
                match entry.Source with
                | InfoView.Provided InfoView.Signature -> "signature"
                | InfoView.Provided InfoView.Use -> "use"
                | InfoView.Provided InfoView.Inferred -> "inferred"
                | InfoView.Provided InfoView.Runtime -> "runtime"
                | InfoView.Attached definitions -> $"""attached {String.concat ", " definitions}"""
                | InfoView.Derived -> "derived"
                | InfoView.BuiltIn -> "built in"
            let provider = if entry.Span.Start < 0 then "" else textAt source entry.Span
            entry.Context, found, provider, entry.Calls |> List.map _.Callee))

/// The texts the editor highlights at the `occurrence`th appearance of `text` in `source`: the providers', then the
/// calls'.
let private highlightsAt (source: string) (text: string) (occurrence: int) =
    let highlights = (ContextFlow.at Ascii (compiled source) (offsetOf source text occurrence 0)).Highlights
    highlights.Providers |> List.map (textAt source), highlights.Calls |> List.map (textAt source)

let private logAndClock =
    """
context Log = (log: string -> ())
context Clock = (now: () -> int)

def note: [Log] string -> () = { s -> log(s) }

def tick: [Clock] () -> int = { now() }

def main: [Log, Clock] () -> int = {
  note("a")
  use Log(log = { s -> () })
  note("b")
  use Log(log = { t -> () })
  tick()
}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``The flow view lists the signature's contexts and each use, with the calls each serves`` () =
    Assert.Equal(
        Some (
            "main",
            [ "Log", "signature", "Log", [ "note" ]
              "Clock", "signature", "Clock", [ "tick" ]
              "Log", "use", "Log(log = { s -> () })", [ "note" ]
              "Log", "use", "Log(log = { t -> () })", [] ]
        ),
        flowAt logAndClock "tick()" 0)

[<Fact>]
let ``The flow view shows what an unannotated def was inferred to require`` () =
    let source = withFinalNewline """def greet = {
  println("a")
  println("b")
}"""
    Assert.Equal(
        Some (
            "greet",
            [ "Console", "inferred", "greet", [ "println"; "println" ]
              "ToString(string)", "attached string.toString", "", [ "println"; "println" ] ]
        ),
        flowAt source "println" 0)

let private showContext =
    """
context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def describe: [Show(a)] a -> string = { x -> show(x) }
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``The flow view shows an instance built from attached definitions, naming them, with the calls it serves`` () =
    let source = showContext + """
def Point.show: Point -> string = { p -> "p" }

def main = {
  describe(Point(x = 1, y = 2))
  describe(Point(x = 3, y = 4))
}
"""
    Assert.Equal(
        Some ("main", [ "Show(Point)", "attached Point.show", "", [ "describe"; "describe" ] ]),
        flowAt source "describe(Point" 0)

[<Fact>]
let ``The flow view shows a context from a use with the use, not as an instance the compiler builds`` () =
    let source = showContext + """
def main = {
  use Show(Point)(show = { p -> "p" })
  describe(Point(x = 1, y = 2))
}
"""
    Assert.Equal(
        Some ("main", [ "Show(Point)", "use", """Show(Point)(show = { p -> "p" })""", [ "describe" ] ]),
        flowAt source "describe(Point" 0)

// The flow wrote a context's associated types by position, `Num(a, fraction)`, where hover writes them by name (#3047).
[<Fact>]
let ``The flow view writes a context's associated type as hover does, bound by name`` () =
    let source = withFinalNewline "def ratio: [Num(a)] a, a -> fraction = { x, y -> x / y }"
    Assert.Equal(Some ("ratio", [ "Num(a, fraction = b)", "signature", "Num(a)", [] ]), flowAt source "x / y" 0)

[<Fact>]
let ``There is no flow outside a def`` () =
    Assert.Equal(None, flowAt "println(\"top\")\n" "println" 0)

[<Fact>]
let ``On a use, the calls it serves are highlighted`` () =
    Assert.Equal<string list * string list>(([ "Log(log = { s -> () })" ], [ "note" ]), highlightsAt logAndClock "use Log" 0)
    Assert.Equal<string list * string list>(([ "Log(log = { t -> () })" ], []), highlightsAt logAndClock "use Log" 1)

[<Fact>]
let ``On a call, what provides its contexts is highlighted`` () =
    Assert.Equal<string list * string list>(([ "Log" ], [ "note" ]), highlightsAt logAndClock """note("a")""" 0)
    Assert.Equal<string list * string list>(([ "Log(log = { s -> () })" ], [ "note" ]), highlightsAt logAndClock """note("b")""" 0)
