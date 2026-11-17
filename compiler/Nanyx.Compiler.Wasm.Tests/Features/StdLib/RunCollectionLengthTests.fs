module TranspilerWasmTests.Features.StdLib.RunCollectionLength

open Xunit
open TranspilerWasmTestHelpers

// #1769: every collection gives its size as `length`, maps, sets, `MList` and `MStack` among them.

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - a map, a set, an MList and an MStack give their size as length`` () =
    run
        """
import nanyx/mlist
import nanyx/mstack

@entry
def main = {
  def m = ["a" => 1, "b" => 2, "c" => 3]
  def s = set.fromList([1, 2, 2, 3, 3])
  def sizes = memory {
    def items = MList.empty()
    items += 4
    items += 5
    def stack = MStack.empty()
    stack \push("x")
    (MList.length(items), MStack.length(stack))
  }
  println("{map.length(m)} {set.length(s)} {m \map.length} {s \set.length} {sizes}")
}
"""
    |> assertRan "3 3 3 3 (2, 1)\n"
