/// A spread in a collection literal takes anything that can be iterated: a list, an array, a sequence, a set, a slice
/// or a type that attaches `toSeq` (#1869).
module TranspilerWasmTests.Features.Collections.Spreads

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a list literal spreads a set, a range and a type that attaches toSeq`` () =
    runSource
        """
type Bag = protected (items: list(int))

def Bag.toSeq: Bag -> seq(int) = { b -> list.toSeq(b.items) }

def s = set[1, 2]
def xs = [...s, 3]
def ys = [0, ...(1..3), ...Bag(items = [9])]

println(xs)
println(ys)
"""
    |> assertRan "[1, 2, 3]\n[0, 1, 2, 3, 9]\n"

[<Fact>]
let ``Run - a literal of another type spreads a set, a slice and a sequence`` () =
    runSource
        """
import nanyx/slice

def s = set[1, 2]

println(Array[...s, ...Array.slice(Array[7, 8, 9], 1, 3)])
println(set[...(5..7), 5])
"""
    |> assertRan "[1, 2, 8, 9]\n[5, 6, 7]\n"

[<Fact>]
let ``Run - a literal spreads a sequence that needs a context, and a mutable slice`` () =
    runSource
        """
import (
  nanyx/marray
  nanyx/mslice
)

@entry
def main: [Console] () -> () = {
  def loud = (1..2) \seq.map { x ->
    println("pull {x}")
    x
  }
  println([...loud, 0])
  memory {
    def m = MArray[1, 2, 3]
    println(Array[...MArray.slice(m, 0, 2)])
  }
}
"""
    |> assertRan "pull 1\npull 2\n[1, 2, 0]\n[1, 2]\n"
