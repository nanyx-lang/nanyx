/// Doc comments, whose text and line breaks the formatter keeps, with their continuation lines one indent step past
/// their first line.
module Nanyx.Formatter.Tests.DocCommentTests

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A doc comment's continuation lines are set one indent step past its first line`` () =
    formatsTo
        """--| The `length` bytes of `s` starting at byte `start`.
  Out-of-range values are clamped.
def substring = 1"""
        """--| The `length` bytes of `s` starting at byte `start`.
        Out-of-range values are clamped.
def substring = 1"""

[<Theory>]
[<InlineData(1)>]
[<InlineData(3)>]
[<InlineData(6)>]
let ``A doc comment whose continuation lines are indented other than one step is laid out again`` (indent: int) =
    formatsTo
        """--| Doc.
  more.
def x = 1"""
        ($"""--| Doc.
{System.String(' ', indent)}more.
def x = 1""")

[<Fact>]
let ``A doc comment keeps its blank lines and the indents of its lines relative to each other`` () =
    formatsTo
        """--| Adds one.

  For example:

      inc(1) == 2
def inc = { n -> n + 1 }"""
        """--| Adds one.

     For example:

         inc(1) == 2
def inc = { n -> n + 1 }"""

[<Fact>]
let ``A doc comment inside a block is indented with what it documents`` () =
    formatsTo
        """def f = {
  --| A local def.
    More about it.
  def g = 1
  g
}"""
        """def f = {
    --| A local def.
     More about it.
    def g = 1
    g
}"""

[<Fact>]
let ``A doc comment on a field keeps its place`` () =
    formatsTo
        """type Point = (
  --| Across.
    In pixels.
  x: int
  y: int
)"""
        """type Point = (
  --| Across.
    In pixels.
  x: int
  y: int
)"""
