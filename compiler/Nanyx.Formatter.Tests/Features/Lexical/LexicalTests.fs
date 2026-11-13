/// Digit separators and block comments, which the formatter keeps exactly as they are written.
module Nanyx.Formatter.Tests.LexicalTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Numbers grouped with underscores are kept as written`` () =
    unchanged (normalized """def million = 1_000_000
def pi = 3.141_592
def big = 1_000.5e1_0
def small = -1_000""")

[<Fact>]
let ``Block comments at the top level are kept as written`` () =
    unchanged (normalized """---
This is a multi-line comment.
   Its lines keep their indent,

and its blank lines.
---

----------------------------------
This is also a multi-line comment.
---
----------------------------------
def a = 1""")

[<Fact>]
let ``A block comment inside a lambda keeps its lines as written`` () =
    unchanged (normalized """def f = { x ->
  ---
  one

      two
  ---
  x + 1
}""")

[<Fact>]
let ``A block comment's opening line is indented with the code, and the rest is left as it is`` () =
    let source = normalized """def f = { x ->
    ---
one
    ---
    x + 1
}"""
    let expected = normalized """def f = { x ->
  ---
one
    ---
  x + 1
}"""
    Assert.Equal(expected, format source)
