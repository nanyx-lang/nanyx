/// A mismatch against a type an annotation writes points back at where the type is written, as a related location: a
/// def's value against its type, and a def's lambda's body against the return type its type gives (#2717).
module Nanyx.Compiler.Tests.Features.Inference.AnnotationRelated

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic of `source`, which must be a type mismatch: the text it is at, and the text and label of each of
/// its related locations.
let private reported (source: string) =
    let source = withFinalNewline source
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    textAt source diagnostic.Span.Value, diagnostic.Related |> List.map (fun (span, label) -> textAt source span, label)

[<Fact>]
let ``A def's value of another type than its annotation's points at the annotation`` () =
    Assert.Equal(("\"three\"", [ "int", "the type written here" ]), reported "def count: int = \"three\"")

[<Fact>]
let ``A lambda's body of another type than the return type points at the return type`` () =
    Assert.Equal(("n + 1", [ "string", "the type written here" ]), reported "def f: int -> string = { n -> n + 1 }")
    Assert.Equal(("def m = n * 2\n  m", [ "string", "the type written here" ]), reported "def k: int -> string = { n ->\n  def m = n * 2\n  m\n}")

[<Fact>]
let ``A local def's value of another type than its annotation's points at the annotation`` () =
    Assert.Equal(("x", [ "string", "the type written here" ]), reported "def g = { x: int ->\n  def y: string = x\n  y\n}")

[<Fact>]
let ``A mismatch at the same place that isn't the annotation's points at nothing`` () =
    // An operator's mismatch in the body is reported where the body is, but the annotation didn't cause it.
    Assert.Equal(("n + \"a\"", []), reported "def h: int -> int = { n -> n + \"a\" }")
    Assert.Equal(("\"a\" ++ n", []), reported "def j: int -> string = { n -> \"a\" ++ n }")

[<Fact>]
let ``Arms that disagree point at the arms before the odd one, not at the return type`` () =
    // The arms disagree whatever the return type is, so it isn't the annotation's constraint that fails.
    Assert.Equal(
        ("2", [ "\"zero\"", "this arm gives 'string'" ]),
        reported "def label: int -> string = { n ->\n  match n\n    | 0 -> \"zero\"\n    | _ -> 2\n}")

[<Fact>]
let ``An argument of another type than the callee's parameter points at the type the callee writes for it`` () =
    // In the callee's type, or as its lambda's parameter's annotation (#3109).
    Assert.Equal(("2", [ "string", "the type 'f' takes here" ]), reported "def f: int, string -> int = { n, s -> n }\ndef r = f(1, 2)")
    Assert.Equal(("\"a\"", [ "int", "the type 'g' takes here" ]), reported "def g = { x: int -> x + 1 }\ndef r = g(\"a\")")

[<Fact>]
let ``A lambda argument whose parameter's annotation doesn't fit points at it and at the callee's parameter`` () =
    Assert.Equal(
        ("{ x: int -> \"a\" }", [ "string -> string", "the type 'apply' takes here"; "int", "the type written here" ]),
        reported "def apply: (string -> string), string -> string = { f, s -> f(s) }\ndef r = apply({ x: int -> \"a\" }, \"s\")")

[<Fact>]
let ``An argument of a callee that writes no type for it points at nothing`` () =
    Assert.Equal(("n + 1", []), reported "def k = { x -> x }\ndef h: int -> string = { n -> k(n + 1) }")

[<Fact>]
let ``An argument of another type than an imported callee's parameter points at the type in the callee's file`` () =
    let source = withFinalNewline "def r = list.length(\"a\")"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    let span, label = Assert.Single diagnostic.Related
    Assert.Equal(("nanyx/prelude-lists.nyx", "the type 'list.length' takes here"), (span.File, label))
