module Nanyx.Compiler.Tests.Features.Tooling.ContextFlow

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The info view's flow view: each provider of contexts in a def, with the calls it serves, and the highlights between
// a provider and its calls.

let private compiled (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result

/// The flow of the def around the `occurrence`th appearance of `text` in `source`: the def's name, and each provider as
/// its context, its source, the text of its span, and the names of the calls it serves.
let private flowAt (source: string) (text: string) (occurrence: int) =
    (ContextFlow.at Ascii (compiled source) (offsetOf source text occurrence 0)).Flow
    |> Option.map (fun flow ->
        flow.Def,
        flow.Entries
        |> List.map (fun entry ->
            let found =
                match entry.Source with
                | InfoView.Signature -> "signature"
                | InfoView.Use -> "use"
                | InfoView.Inferred -> "inferred"
                | InfoView.Runtime -> "runtime"
            entry.Context, found, textAt source entry.Span, entry.Calls |> List.map _.Callee))

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

def quiet: Log = (log = { s -> () })

def main: [Log & Clock] () -> int = {
  note("a")
  use quiet
  note("b")
  use Log(log = { s -> () })
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
              "Log", "use", "quiet", [ "note" ]
              "Log", "use", "Log(log = { s -> () })", [] ]
        ),
        flowAt logAndClock "tick()" 0)

[<Fact>]
let ``The flow view shows what an unannotated def was inferred to require`` () =
    let source = "import nanyx/console\n\ndef greet = {\n  println(\"a\")\n  println(\"b\")\n}\n"
    Assert.Equal(Some ("greet", [ "Console", "inferred", "greet", [ "println"; "println" ] ]), flowAt source "println" 0)

[<Fact>]
let ``There is no flow outside a def`` () =
    Assert.Equal(None, flowAt "import nanyx/console\n\nprintln(\"top\")\n" "println" 0)

[<Fact>]
let ``On a use, the calls it serves are highlighted`` () =
    Assert.Equal<string list * string list>(([ "quiet" ], [ "note" ]), highlightsAt logAndClock "use quiet" 0)
    Assert.Equal<string list * string list>(([ "Log(log = { s -> () })" ], []), highlightsAt logAndClock "use Log" 0)

[<Fact>]
let ``On a call, what provides its contexts is highlighted`` () =
    Assert.Equal<string list * string list>(([ "Log" ], [ "note" ]), highlightsAt logAndClock "note(\"a\")" 0)
    Assert.Equal<string list * string list>(([ "quiet" ], [ "note" ]), highlightsAt logAndClock "note(\"b\")" 0)
