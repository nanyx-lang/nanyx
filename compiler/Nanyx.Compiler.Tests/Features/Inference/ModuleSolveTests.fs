module Nanyx.Compiler.Tests.Features.Inference.ModuleSolve

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A module's constraints are solved a def at a time: each def's on top of what the defs before it solved to, where the
// two share no variable (#1266). That must find what solving the whole module afresh finds: the same failure, reported
// at the same place, and a union's tags in the same order.

/// `count` defs, from `f{from}` on, that each pass a lambda to `list.map`, whose contexts get rests made up for them.
let private mapDefs (from: int) (count: int) =
    [ for i in from .. from + count - 1 -> $"def f{i} = list.map([1, 2, 3], {{ x -> x + {i} }}) \\list.length" ]

let private moduleOf (lines: string list) = String.concat "\n" ("module m" :: "" :: lines) + "\n"

[<Fact>]
let ``A failure among many defs is reported where solving the module afresh reports it`` () =
    let source = moduleOf (mapDefs 1 30 @ [ "def bad: string = list.map([1, 2, 3], { x -> x + 1 }) \\list.length" ] @ mapDefs 31 30)
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'int' vs 'string'", "list.map([1, 2, 3], { x -> x + 1 }) \\list.length"),
        onlyDiagnostic source)

[<Fact>]
let ``A union built across defs keeps its tags in the order solving the module afresh gives`` () =
    let source =
        moduleOf (
            mapDefs 1 20
            @ [ "def colour = { n: int -> if n > 0 then #red else if n < 0 then #green else #blue }"
                "def shade = { n: int -> if n > 10 then #dark else colour(n) }" ]
            @ mapDefs 21 20
            @ [ "def tint = { n: int -> if n > 5 then shade(n) else #light }" ])
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.Equal("int -> #red | #green | #blue", typeIn result.Typed.Value "colour")
    Assert.Equal("int -> #dark | #red | #green | #blue", typeIn result.Typed.Value "shade")
    Assert.Equal("int -> #dark | #red | #green | #blue | #light", typeIn result.Typed.Value "tint")

[<Fact>]
let ``Defs that share a variable, as mutually recursive ones do, are solved together`` () =
    let source =
        moduleOf (
            mapDefs 1 10
            @ [ "def isEven = { n -> if n == 0 then true else isOdd(n - 1) }"
                "def isOdd = { n -> if n == 0 then false else isEven(n - 1) }" ]
            @ mapDefs 11 10)
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.Equal("int -> bool", typeIn result.Typed.Value "isEven")
    Assert.Equal("int -> bool", typeIn result.Typed.Value "isOdd")
