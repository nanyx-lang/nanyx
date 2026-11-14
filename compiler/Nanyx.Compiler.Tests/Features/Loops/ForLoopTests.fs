module Nanyx.Compiler.Tests.Features.Loops.ForLoops

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `for xs, ys | x, y -> ...` walks several iterables in step, and `a..` is a range without an end (a `gen`).

let private typesOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value.Types

/// The code of the one diagnostic `source` compiles to.
let private singleCode (source: string) =
    let result = Compiler.compile source
    Assert.Equal(1, result.Diagnostics.Length)
    result.Diagnostics.Head.Code

[<Fact>]
let ``a range without an end is a gen of ints`` () =
    let types = typesOf "def naturals = 0..\ndef later = 1 + 2.."
    Assert.Equal("gen(int)", Unifier.tyToString types.["naturals"])
    Assert.Equal("gen(int)", Unifier.tyToString types.["later"])

[<Fact>]
let ``each arm of a zipped loop matches one element of each iterable`` () =
    typesOf (
        withFinalNewline """def label: list(string) -> () = { names -> for names, 0.. | name, i -> name ++ "{i}" }
def pairs = for [true], 1..3, ["a"] | flag, n, s -> if flag then n + 1 else 0
def both = { xs, ys -> for xs, ys | x, y -> x + y }""")
    |> ignore

[<Fact>]
let ``an element of a zipped loop has its own iterable's type`` () =
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, singleCode """def f = for ["a"], 0.. | s, i -> i ++ s""")

[<Fact>]
let ``a loop over a gen alone is an error`` () =
    Assert.Equal(DiagnosticCodes.EndlessForLoop.Code, singleCode "def f = for 0.. | i -> i")
    Assert.Equal(DiagnosticCodes.EndlessForLoop.Code, singleCode "def g = for 0.., 1.. | i, j -> i + j")

[<Fact>]
let ``an arm of a zipped loop needs a pattern for each iterable`` () =
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, singleCode "def f = for [1], [2] | x -> x")
