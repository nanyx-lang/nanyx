module Nanyx.Compiler.Tests.Features.StdLib.Sequences

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The prelude's lazy sequence, `seq(a)`, and ranges as values of it (#192).

/// `source`, its line endings made `\n`, compiled with no errors.
let private clean (source: string) = compileClean (source.ReplaceLineEndings "\n")

[<Fact>]
let ``A range is a seq of ints, written as the prelude's type is`` () =
    let typed =
        clean """
def bounded = 1..10
def endless = 0..
def written: Sequence(int) = 1..3
def aliased: seq(int) -> Sequence(int) = { s -> s }
"""
    Assert.Equal("seq(int)", asciiTypeIn typed "bounded")
    Assert.Equal("seq(int)", asciiTypeIn typed "endless")
    Assert.Equal("seq(int)", asciiTypeIn typed "written")
    Assert.Equal("seq(int) -> seq(int)", asciiTypeIn typed "aliased")

// A range's bounds are inferred once, so a range in a range's bound checks in time that grows with how deep it is
// nested. Each level was inferred twice, and 18 levels took 113 s to check (#2307).
[<Fact>]
let ``A range nested 20 deep in its own bound is a seq of ints`` () =
    let nested = List.fold (fun inner _ -> $"seq.length(1..{inner})") "3" [ 1..20 ]
    let typed = clean $"def counted = {nested}\ndef ranged = 1..{nested}\n"
    Assert.Equal("int", asciiTypeIn typed "counted")
    Assert.Equal("seq(int)", asciiTypeIn typed "ranged")

/// `U8` and `float` made `Enumerable`, so that ranges of them can be written.
let private enumerableNumbers = "def U8.succ: U8 -> U8 = { n -> n + 1 }\ndef float.succ: float -> float = { x -> x + 1.0 }\n"

[<Fact>]
let ``A range's bounds are of their own type, whatever the range is used as`` () =
    let typed =
        clean (
            enumerableNumbers
            + """def decimals: seq(float) = 0.5..2.5
def ints: seq(I64) = 1..3
def bytes = { first: U8, last: U8 -> first..last }
def countInts = { xs: seq(I64) -> seq.length(xs) }
def counted = countInts(1..5)
"""
        )
    Assert.Equal("seq(float)", asciiTypeIn typed "decimals")
    Assert.Equal("seq(int)", asciiTypeIn typed "ints")
    Assert.Equal("U8, U8 -> seq(U8)", asciiTypeIn typed "bytes")
    Assert.Equal("int", asciiTypeIn typed "counted")

[<Fact>]
let ``A range of number literals is of ints, not of the numbers it is used as`` () =
    let cases =
        [ "def small: seq(U8) = 1..4\n", "'int' vs 'U8'", "1..4"
          "def floats: seq(float) = 1..3\n", "'int' vs 'float'", "1..3"
          "def countBytes = { xs: seq(U8) -> seq.length(xs) }\ndef n = countBytes(250..254)\n", "'int' vs 'U8'", "250..254"
          "def mixed = 1..2.5\n", "'int' vs 'float'", "1" ]
    for source, message, at in cases do
        Assert.Equal((DiagnosticCodes.TypeMismatch.Code, message, at), onlyDiagnostic (enumerableNumbers + source))

[<Fact>]
let ``The seq functions are attached to seq, and a pipe finds them`` () =
    let typed =
        clean """
def doubled = (1..10) \seq.map { * 2 } \seq.toList
def piped: [Console] () -> list(int) = { (1..3) \map { x -> x + 1 } \toList }
def firstEvens = (0..) \seq.filter { x -> x % 2 == 0 } \seq.take(3) \Sequence.toList
"""
    Assert.Equal("list(int)", asciiTypeIn typed "doubled")
    Assert.Equal("list(int)", asciiTypeIn typed "firstEvens")

[<Fact>]
let ``A sequence mapped with a function that needs a context carries it`` () =
    let typed =
        clean """
def loud = { () ->
  (1..3) \seq.map { x ->
    println("at {x}")
    x
  }
}
"""
    Assert.Equal("() -> [Console] seq(int)", asciiTypeIn typed "loud")

[<Fact>]
let ``A sequence that carries a context is pulled only where the context is`` () =
    let diagnostic =
        diagnosticOf (
            """
def pulled: ([Console] seq(int)) -> list(int) = { s -> s \seq.toList }
""".ReplaceLineEndings "\n"
        )
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
    Assert.Contains("'Sequence.toList' requires the context 'Console'", diagnostic.Message)

[<Fact>]
let ``A sequence mapped with a function that needs a context is made where the context is not`` () =
    // #1738: making the sequence runs nothing, so a pure function or a top-level value can make one.
    let typed =
        clean """
def loud: [Console] seq(int) = (1..3) \seq.map { x ->
  println("pull {x}")
  x * 10
}

def made: () -> [Console] seq(int) = {
  (1..3) \seq.map { x ->
    println("pull {x}")
    x * 10
  }
}
"""
    Assert.Equal("[Console] seq(int)", asciiTypeIn typed "loud")
    Assert.Equal("() -> [Console] seq(int)", asciiTypeIn typed "made")

[<Fact>]
let ``A sequence mapped with a function that needs a context is still pulled only where the context is`` () =
    let diagnostic =
        diagnosticOf (
            """
def made: () -> [Console] seq(int) = {
  (1..3) \seq.map { x ->
    println("pull {x}")
    x
  }
}

def pulled: () -> list(int) = { made() \seq.toList }
""".ReplaceLineEndings "\n"
        )
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
    Assert.Contains("requires the context 'Console'", diagnostic.Message)
