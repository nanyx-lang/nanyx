module Nanyx.Formatter.Tests.MemoryTests

open Xunit
open Nanyx.Formatter

/// `source` formatted, with line breaks `\n`. `source` is written from its second line.
let private format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" (source.ReplaceLineEndings "\n").[1..] with
    | Result.Ok formatted -> formatted.ReplaceLineEndings "\n"
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// `expected`, written from its second line, as `format` gives it.
let private text (expected: string) = (expected.ReplaceLineEndings "\n").[1..]

[<Fact>]
let ``A memory block with mut and := keeps its layout`` () =
    let source = """
def sum: list(int) -> int = { xs ->
  memory {
    mut total = 0
    for xs | x -> total := total + x
    total
  }
}
"""
    Assert.Equal(text source, format source)

[<Fact>]
let ``Spacing around mut and := is made regular`` () =
    let source = """
def f = {
  mut   a:int =   x +  1
  a:=a  *  2
  a
}
"""
    let expected = """
def f = {
  mut a: int = x + 1
  a := a * 2
  a
}
"""
    Assert.Equal(text expected, format source)

[<Fact>]
let ``An assignment or mut before the last argument is bracketed`` () =
    let source = """
def f = {
  g((a := 1), 2)
}
"""
    Assert.Equal(text source, format source)
