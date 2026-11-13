module Nanyx.Formatter.Tests.MemoryTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

/// `source`, written from its second line, with line breaks `\n`.
let private text (source: string) = (source.ReplaceLineEndings "\n").[1..]

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
    Assert.Equal(text source, format (text source))

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
    Assert.Equal(text expected, format (text source))

[<Fact>]
let ``An assignment or mut before the last argument is bracketed`` () =
    let source = """
def f = {
  g((a := 1), 2)
}
"""
    Assert.Equal(text source, format (text source))


[<Fact>]
let ``A cell parameter, a cell argument and a cell type keep their layout`` () =
    let source = """
def inc: [$Memory] mut int -> () = { mut cell ->
  cell := cell + 1
}

def add = { mut total: int, n -> total := total + n }

def f = memory {
  mut count = 0
  inc(mut count)
  add(mut count, 2)
  count
}
"""
    Assert.Equal(text source, format (text source))