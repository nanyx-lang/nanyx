module Nanyx.Formatter.Tests.Features.Collections.Collections

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A list with its ] on a line of its own is joined onto one line when it fits`` () =
    let source = """
def xs = [
  1,
  2
]

def main = {
  def ys = [
    3,
    4
  ]
  ys
}
"""
    let expected = """def xs = [1, 2]

def main = {
  def ys = [3, 4]
  ys
}
"""
    Assert.Equal(expected.ReplaceLineEndings "\n", (format source).ReplaceLineEndings "\n")

[<Fact>]
let ``List spreads are printed as written`` () =
    unchanged "def a = [1, ...xs, 2, ...ys]\n"
    unchanged "def b = [1] ++ xs\n"

[<Fact>]
let ``Map literals are printed as written, one entry per line when they do not fit`` () =
    unchanged (normalized """def m = ["a" => 1, "b" => f(2)]""")
    Assert.Equal("def m = [1 => #one, 2 => #two]\n", format "def m = [1=>#one,2   =>   #two]")
    unchanged (normalized """def prices = [
  "Candy Bar" => Item(price = 12, count = 7)
  "Chips" => Item(price = 10, count = 4)
  "Pretzels" => Item(price = 7, count = 11)
]""")
    unchanged (lines [ "def m = ["; "  -- the first"; "  1 => #one"; "  2 => #two"; "]" ])

[<Fact>]
let ``A record with comments in it gets a line per field`` () =
    Assert.Equal(
        lines [ "def x = ("; "  a = 1"; "  -- the second field"; "  b = 2 -- trailing"; ")" ],
        format (lines [ "def x = ("; "  a = 1"; ""; "  -- the second field"; "  b = 2 -- trailing"; ")" ]))

[<Fact>]
let ``Fields go one per line when one of them spans lines`` () =
    Assert.Equal(
        normalized """def main = {
  use Log(
    log = { message ->
      println("")
      println(message)
    }
  )

  run()
}""",
        format (normalized """def main = {
  use Log(log = { message ->
    println("")
    println(message)
  })
  run()
}"""))
    Assert.Equal(
        lines [ "def pair = ("; "  1"; "  { x ->"; "    x"; "  }"; ")" ],
        format (lines [ "def pair = (1, { x ->"; "  x"; "})" ]))

[<Fact>]
let ``Fields that each fit on one line stay on one line`` () =
    unchanged "def a = f(x = 1, y = { z -> z })\n"
