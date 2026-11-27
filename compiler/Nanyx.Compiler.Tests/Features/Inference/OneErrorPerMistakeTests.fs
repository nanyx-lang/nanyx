module Nanyx.Compiler.Tests.Features.Inference.OneErrorPerMistake

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A value that failed to check has the error type, which fits anywhere without fixing a type variable an annotation
// writes, and no diagnostic comes from it: one mistake is one error, at the mistake (#2534).

/// The codes of the diagnostics compiling `source` gives, and the text each is at.
let private codesAt (source: string) =
    let source = normalized source
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``An unknown name given where an annotation's variable is expected is the one error`` () =
    let source = "def f: (string, a: Type(a)) -> a = { s, a ->\n  def x: a = arts\n  x\n}\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownIdentifier.Code, "arts" ], codesAt source)
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownIdentifier.Code, "missing" ], codesAt "def id: a -> a = { x -> missing }\n")

[<Theory>]
[<InlineData("def apply: (int -> int) -> int = { g -> g(1) }\ndef r = apply({ n -> n + missing })\ndef s: int = r + 1\n", "NYX0101", "missing")>]
[<InlineData("def n: int = \"s\"\ndef m = n + 1\nprintln(m)\n", "NYX0201", "\"s\"")>]
[<InlineData("def add: int, int -> int = { a, b -> a + b }\ndef t = add(1)\ndef u = t + 1\nprintln(u)\n", "NYX0304", "1")>]
[<InlineData("def v = nope\ndef w = v.name.size\nprintln(w)\n", "NYX0101", "nope")>]
let ``A mistake whose value is used afterwards is the one error`` (source: string, code: string, at: string) =
    Assert.Equal<(string * string) list>([ code, at ], codesAt source)
