/// Doc comments, whose text and line breaks the formatter keeps, with their continuation lines one indent step past
/// their first line.
module Nanyx.Formatter.Tests.DocCommentTests

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.FormatCommand

/// `source` with `\n` line breaks, whatever the line endings of this file's checkout, and a final line break.
let private normalized (source: string) = source.Replace("\r\n", "\n") + "\n"

/// Formats `source` as `nanyx format` does, with the check that the result says the same thing with the same comments.
let private format (source: string) =
    match formatText Formatter.Options.defaults "" (normalized source) with
    | Formatted formatted -> formatted
    | other -> failwith $"Not formatted: %A{other}"

/// Formatting `source` gives `expected`, and formatting that gives it back unchanged.
let private formatsTo (expected: string) (source: string) =
    let expected = normalized expected
    Assert.Equal(expected, format source)
    Assert.Equal(expected, format expected)

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
