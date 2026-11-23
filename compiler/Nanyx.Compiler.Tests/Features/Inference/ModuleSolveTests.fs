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

/// `count` defs, from `g{from}` on, that each pipe a list through calls with lambdas, the last with an argument before
/// its lambda, which the type checker looks the types so far up for many times.
let private pipeDefs (from: int) (count: int) =
    [ for i in from .. from + count - 1 ->
          $"def g{i}: list(int) -> int = {{ xs ->\n  xs\n    \\list.map {{ x -> x + {i} }}\n    \\list.filter {{ x -> x > 1 }}\n    \\list.fold(0, {{ a, b -> a + b }})\n}}" ]

/// How long compiling `source` takes, in milliseconds.
let private compileTime (source: string) =
    let watch = System.Diagnostics.Stopwatch.StartNew()
    Compiler.compile source |> ignore
    watch.ElapsedMilliseconds

[<Fact>]
let ``A failure early in a module leaves the defs after it solved on top of what agrees, not afresh`` () =
    // Each type looked up after a failure solved the whole module again, twice, so checking took time in proportion to
    // the square of the module's size, and minutes for a package's (#2449).
    let clean = moduleOf ([ "def first: int = 1" ] @ pipeDefs 1 40)
    let failing = moduleOf ([ "def first: int = \"one\"" ] @ pipeDefs 1 40)
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' vs 'int'", "\"one\""), onlyDiagnostic failing)
    compileTime clean |> ignore
    let cleanTime = compileTime clean
    let failingTime = compileTime failing
    Assert.True(failingTime < 3L * cleanTime + 1000L, $"With the failure: {failingTime} ms; without it: {cleanTime} ms")
