module TranspilerWasmTests.Features.StdLib.RunListSeqFromTo

open Xunit
open TranspilerWasmTestHelpers

// Every collection has `toList`, `fromList` and `fromArray` (#1672): a list's `toList` and `fromList`, and a
// sequence's `fromList` and `fromArray`, which `seq[...]` and `Sequence[...]` build through (#2190).

[<Fact>]
let ``Run - list toList and fromList give the same list`` () =
    let source =
        """@entry
def main = {
  def xs = [1, 2, 3]
  println(xs \list.toList)
  println(list.fromList(xs))
  def none: list(int) = []
  println(none \list.toList \list.length)
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "[1, 2, 3]\n[1, 2, 3]\n0\n"

[<Fact>]
let ``Run - seq fromList and fromArray give the elements in order`` () =
    let source =
        """@entry
def main = {
  println(seq.fromList([1, 2, 3]) \seq.toList)
  println(seq.fromArray(Array[1, 2, 3]) \seq.toList)
  def none: list(int) = []
  println(seq.fromList(none) \seq.toList \list.length)
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "[1, 2, 3]\n[1, 2, 3]\n0\n"

[<Fact>]
let ``Run - a seq or Sequence literal builds a sequence`` () =
    let source =
        """@entry
def main = {
  def s = seq[1, 2, 3]
  println(s \seq.map { * 2 } \seq.toList)
  def t = Sequence[1, 2, 3]
  println(t \seq.toList)
  def expected: seq(string) = ["a", "b"]
  println(expected \seq.toList)
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "[2, 4, 6]\n[1, 2, 3]\n[a, b]\n"
