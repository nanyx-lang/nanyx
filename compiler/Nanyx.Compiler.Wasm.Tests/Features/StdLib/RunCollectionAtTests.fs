module TranspilerWasmTests.Features.StdLib.RunCollectionAt

open Xunit
open TranspilerWasmTestHelpers

// #2186: `at` is the indexed read on every collection, giving `#some` inside the range and `#notFound` for a negative
// index or one past the end.

[<Fact>]
let ``Run - at on a list, a sequence, an array and a slice gives #some or #notFound`` () =
    let source = """
import nanyx/slice

@entry
def main = {
  def xs = [10, 20, 30]
  println((list.at(xs, 1), list.at(xs, -1), list.at(xs, 3), xs \at(0)))
  def s = 10..12
  println((seq.at(s, 2), seq.at(s, -1), seq.at(s, 3), (1..) \seq.at(4)))
  def ys = Array[10, 20, 30]
  println((array.at(ys, 2), array.at(ys, -1), array.at(ys, 3), ys(0), ys(3)))
  def part = ys \slice(1, 3)
  println((Slice.at(part, 1), Slice.at(part, -1), Slice.at(part, 2)))
}
"""
    let expected = """(#some(20), #notFound, #notFound, #some(10))
(#some(12), #notFound, #notFound, #some(5))
(#some(30), #notFound, #notFound, #some(10), #notFound)
(#some(30), #notFound, #notFound)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan (expected.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - at on an MArray, an MList and an MSlice gives #some or #notFound`` () =
    let source = """
import nanyx/marray
import nanyx/mlist
import nanyx/mslice

@entry
def main = {
  def result = memory {
    def xs = MArray.init(3, { i -> i * 10 })
    def items = MList.fromList([1, 2, 3])
    def part = xs \slice(1, 3)
    (
      (MArray.at(xs, 2), MArray.at(xs, -1), MArray.at(xs, 3)),
      (MList.at(items, 0), MList.at(items, -1), MList.at(items, 3)),
      (MSlice.at(part, 0), MSlice.at(part, -1), MSlice.at(part, 2))
    )
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "((#some(20), #notFound, #notFound), (#some(1), #notFound, #notFound), (#some(10), #notFound, #notFound))\n"
