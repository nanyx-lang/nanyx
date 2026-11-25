module Nanyx.Compiler.Tests.Features.TagNotInUnion

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #2689: a tag a closed union doesn't name is reported as not one of its tags, with them listed in the order the union
// writes them, rather than as a mismatch against one of them.

let private color = "type Color = #red | #green | #blue\n\n"

[<Fact>]
let ``A tag passed where a union alias is expected is not one of its tags, which are listed`` () =
    let source = normalized (color + "def paint: Color -> string = { c -> \"x\" }\ndef s = paint(#purple)\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'#purple' is not one of the tags of 'Color': '#red', '#green' or '#blue'", "#purple"),
        onlyDiagnostic source)

[<Fact>]
let ``A tag given for a union written out is not one of the union's tags`` () =
    let source = normalized "def c: #red | #green | #blue = #purple\n"
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'#purple' is not one of the union's tags: '#red', '#green' or '#blue'", "#purple"),
        onlyDiagnostic source)

[<Fact>]
let ``A misspelt tag is asked about the one it is close to`` () =
    let source = normalized (color + "def c: Color = #gren\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'#gren' is not one of the tags of 'Color': '#red', '#green' or '#blue': did you mean '#green'?", "#gren"),
        onlyDiagnostic source)

[<Fact>]
let ``A tag the union names with another payload is reported by its payload`` () =
    let source = normalized "def x: #some(int) | #none = #some(\"x\")\n"
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' vs 'int'", "#some(\"x\")"), onlyDiagnostic source)

[<Fact>]
let ``An open union takes a tag it doesn't name`` () =
    compileClean (normalized "def label: #red | #green | r -> string = { c -> \"x\" }\ndef s = label(#purple)\n") |> ignore
