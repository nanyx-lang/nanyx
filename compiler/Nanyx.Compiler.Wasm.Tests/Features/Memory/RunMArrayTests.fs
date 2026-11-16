module TranspilerWasmTests.Features.Memory.MArrays

open Xunit
open TranspilerWasmTestHelpers

// `MArray(a)` in `nanyx/marray`: an array block whose elements are changed in place. The tests run with memory checks
// on, so an element a set replaces, or one the array holds when it is dropped, that is not given up is reported.

[<Fact>]
let ``Run - an MArray is made, read, set and swapped, and an index out of range is never a trap`` () =
    let source = """
import nanyx/marray

@entry
def main = {
  def result = memory {
    def xs = MArray.init(3, { i -> i * 10 })
    def changed = xs \set(1, 99)
    def missed = xs \set(3, 1)
    def negative = xs \set(-1, 1)
    def swapped = xs \swap(0, 2)
    def notSwapped = xs \swap(0, 3)
    (MArray.toArray(xs), xs \at(1), xs \at(3), xs \at(-1), MArray.length(xs), changed, missed, negative, swapped, notSwapped)
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([20, 99, 0], #some(99), #none, #none, 3, #ok, #outOfRange, #outOfRange, #ok, #outOfRange)\n"

[<Fact>]
let ``Run - sets of strings give up the strings they replace, and converting copies`` () =
    let source = """
import nanyx/array
import nanyx/marray

@entry
def main = {
  def result = memory {
    def original = array.fromList(["a", "b", "c"])
    def ws = MArray.fromArray(original)
    def first = ws \set(0, "x")
    def second = ws \set(0, "y")
    def swapped = ws \swap(1, 2)
    def copied = MArray.toArray(ws)
    def after = ws \set(2, "z")
    (original, copied, MArray.toList(ws))
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([a, b, c], [y, c, b], [y, c, z])\n"

[<Fact>]
let ``Run - create fills each element with its type's default`` () =
    let source = """
import nanyx/marray

@entry
def main = {
  def result = memory {
    def counts: MArray(int) = MArray.create(3)
    def names: MArray(string) = MArray.create(2)
    def changed = names \set(1, "n")
    def empty: MArray(int) = MArray.create(0)
    (MArray.toList(counts), MArray.toList(names), MArray.length(empty))
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([0, 0, 0], [, n], 0)\n"
