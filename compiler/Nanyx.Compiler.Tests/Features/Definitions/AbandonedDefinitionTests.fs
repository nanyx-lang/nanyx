/// A top-level def that fails to check: it is of the error type, and requires no contexts.
module Nanyx.Compiler.Tests.Features.AbandonedDefinition

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The codes of the diagnostics compiling `source` gives, in order.
let private codes (source: string) =
    (Compiler.compile (normalized source)).Diagnostics |> List.map _.Code

// A def that failed to check kept the contexts its annotation writes, so a call to it, of the error type, reported
// each as missing too, and hover wrote them over the def's broken type (#2879).
[<Fact>]
let ``A call to a def whose annotation is too general reports only that`` () =
    let source =
        normalized """dimension Distance

@baseUnit unit cm: Distance

dimension Time

@baseUnit unit s: Time

def speed: Distance(a), Time(b) -> _ = { d, t -> d / t }

def v = speed(100cm, 2s)
"""
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.AnnotationTooGeneral.Code ], result.Diagnostics |> List.map _.Code)
    let hover = Hover.typeAt Unicode result (offsetOf source "speed" 0 0) |> Option.map _.Type
    Assert.Equal(Some "def speed: Distance(a), Time(a) -> Distance(α) / Time(α)", hover)

[<Fact>]
let ``A call to a def that leaves a context's type unsettled reports only that`` () =
    let source =
        """context Foo(a) = (bar: (a, a -> a))

def unused: [Foo(_)] a -> a = { x -> x }

def n = unused(1)
"""
    Assert.Equal<string list>([ DiagnosticCodes.UndeterminedContextType.Code ], codes source)
