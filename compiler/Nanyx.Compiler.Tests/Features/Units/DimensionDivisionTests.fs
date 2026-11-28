/// A def that divides or multiplies two of its parameters and writes neither their types nor its own reads the operator
/// through `Num`, which takes its operands as one type, so a call with two dimensions is a mismatch that says to write
/// the parameters' types, and shows the def with them written (#2880).
module Nanyx.Compiler.Tests.Features.DimensionDivision

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private distance = "dimension Distance\n@baseUnit unit cm: Distance\n\n"

[<Fact>]
let ``Dividing two dimensions in a def that writes no types is one mismatch that shows the def with them written`` () =
    let source = withFinalNewline (distance + "def speed = { d, t -> d / t }\n\nprintln(speed(100cm, 2s))")
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
    let diagnostic = result.Diagnostics.Head
    Assert.Equal("2s", textAt source (spanOf diagnostic))
    Assert.Equal(
        "'speed' takes 'd' and 't' as one type, since it divides them with `/` and doesn't write their types, but is given a 'Distance(int)' and a 'Time(int)'. To divide two dimensions, write the parameters' types: `def speed = { d: Distance(int), t: Time(int) -> d / t }`",
        diagnostic.Message)
    Assert.Equal<string list>([ "/" ], diagnostic.Related |> List.map (fun (at, _) -> textAt source at))

[<Fact>]
let ``The def the message shows compiles`` () =
    compileClean (withFinalNewline (distance + "def speed = { d: Distance(int), t: Time(int) -> d / t }\n\ndef v = speed(100cm, 2s)")) |> ignore

[<Fact>]
let ``Multiplying says so, and a long body is left out of the def it shows`` () =
    let source =
        withFinalNewline (distance + "def area = { a, b, scale -> a * b + 0cm * 0s * scale * 1000000000000000000 * 1000000000000000000 }\n\ndef x = area(1cm, 2s, 3)")
    let diagnostic = (Compiler.compile source).Diagnostics |> List.find (fun diagnostic -> diagnostic.Message.Contains "multipl")
    Assert.EndsWith("To multiply two dimensions, write the parameters' types: `def area = { a: Distance(int), b: Time(int), scale -> ... }`", diagnostic.Message)

[<Fact>]
let ``Two numbers, and two dimensions the def doesn't divide, are not this mismatch`` () =
    compileClean (withFinalNewline (distance + "def ratio = { a, b -> a / b }\n\ndef r = ratio(4.0, 2.0)")) |> ignore
    let source = withFinalNewline (distance + "def pair = { a, b -> [a, b] }\n\ndef p = pair(1cm, 2s)")
    let diagnostic = (Compiler.compile source).Diagnostics.Head
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.DoesNotContain("write the parameters' types", diagnostic.Message)
