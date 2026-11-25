/// Where a tuple is written in brackets: only where the parser needs them (#2653).
module Nanyx.Formatter.Tests.Features.TupleBrackets

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A destructuring def's tuple has no brackets, at the top level and in a block`` () =
    unchanged "def x, y = 1, 2\n\ndef main = {\n  def a, b = 3, 4\n  def (e, f) = 7, 8\n  a + b + e + f\n}\n"

[<Fact>]
let ``A destructuring def's tuple written in brackets is written without them`` () =
    "def main = {\n  def c, d = (5, 6)\n  c + d\n}" |> formatsTo "def main = {\n  def c, d = 5, 6\n  c + d\n}"

[<Fact>]
let ``A destructuring def's tuple too long for its line is broken in brackets`` () =
    let formatted = formatWith 30 "def first, second = someLongName, anotherLongName\n"
    Assert.Equal("def first, second = (\n  someLongName\n  anotherLongName\n)\n", formatted)

[<Fact>]
let ``A def's tuple has no brackets, with a type or without, and a cell's neither`` () =
    "def point = (3, 4)\n\ndef p: (int, int) = (3, 4)\n\ndef main = {\n  def q = (1, \"a\")\n  memory {\n    mut m = (1, 2)\n    m\n  }\n}"
    |> formatsTo "def point = 3, 4\n\ndef p: int, int = 3, 4\n\ndef main = {\n  def q = 1, \"a\"\n\n  memory {\n    mut m = 1, 2\n    m\n  }\n}"

[<Fact>]
let ``A tuple keeps its brackets where the parser needs them`` () =
    unchanged "def main = {\n  memory {\n    mut m = 1, 2\n    m := (3, 4)\n    m\n  }\n}\n"
    unchanged "def f = { x -> (x, 1) }\n"
    unchanged "def g = { x ->\n  match x\n    | 0 -> (1, 2)\n    | _ -> (3, 4)\n}\n"
    unchanged "def h = { x ->\n  def y = x\n  (y, 2)\n}\n"
    unchanged "def pairs = [(1, 2), (3, 4)]\n"
