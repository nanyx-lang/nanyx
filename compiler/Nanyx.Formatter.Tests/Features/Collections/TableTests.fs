/// Table literals (#1255): one row per line, with each column's `|` lined up.
module Nanyx.Formatter.Tests.Features.Collections.Tables

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A formatted table is printed as it is`` () =
    unchanged (
        normalized
            """def namesAndAges =
  [| name     | age |
   | "Alex"   | 22  |
   | "Taylor" | 30  |
   | "Sam"    | 28  |
   | "Morgan" | 45  |]"""
    )

[<Fact>]
let ``A table's columns are lined up and its rows indented one column in from its line`` () =
    """def t =
  [| name | age |
      | "Alex" | 22 |
  | "Taylor"   | 1 + 2 |]"""
    |> formatsTo
        """def t =
  [| name     | age   |
   | "Alex"   | 22    |
   | "Taylor" | 1 + 2 |]"""
    // A table written after the `=` stays there, as any other value does.
    """def t = [| name | age |
      | "Alex" | 22 |]"""
    |> formatsTo
        """def t = [| name   | age |
 | "Alex" | 22  |]"""

[<Fact>]
let ``A cell is printed as its expression is`` () =
    """def t =
  [| a       | b |
   | f(x,y) | [1,2] |]"""
    |> formatsTo
        """def t =
  [| a       | b      |
   | f(x, y) | [1, 2] |]"""

[<Fact>]
let ``A table in a block or a call keeps its layout`` () =
    """def main = {
  def rows =
    [| x | y |
     | 1 | 2 |]
  sum([| x | y |
   | 1 | 2 |])
}"""
    |> formatsTo
        """def main = {
  def rows =
    [| x | y |
     | 1 | 2 |]

  sum(
    [| x | y |
     | 1 | 2 |]
  )
}"""

[<Fact>]
let ``A table with a comment among its rows is printed as the list it stands for`` () =
    """def t =
  [| a | b |
   -- the first
   | 1 | 2 |]"""
    |> formatsTo
        """def t =
  [
    -- the first
    (a = 1, b = 2)
  ]"""
