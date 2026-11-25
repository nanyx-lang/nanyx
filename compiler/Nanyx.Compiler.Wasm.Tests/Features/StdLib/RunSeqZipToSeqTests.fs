module TranspilerWasmTests.Features.StdLib.RunSeqZipToSeq

open Xunit
open TranspilerWasmTestHelpers

// `seq.zip` pairs two sequences, and `set.toSeq` and `map.toSeq` turn a set and a map into sequences (#1795).

[<Fact>]
let ``Run - seq.zip stops at the shorter sequence, and ends an endless one`` () =
    let source =
        """@entry
def main = {
  println(seq.zip(1..5, list.toSeq(["a", "b", "c"])) \seq.toList)
  println(seq.zip(list.toSeq(["a"]), 1..5) \seq.toList)
  println(seq.zip(0.., list.toSeq(["a", "b"])) \seq.toList)
  def none: seq(int) = seq.empty()
  println(seq.zip(none, 0..) \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[(1, a), (2, b), (3, c)]\n[(a, 1)]\n[(0, a), (1, b)]\n[]\n"

[<Fact>]
let ``Run - seq.zip pulls one element from each side per pair, and keeps the sequences' contexts`` () =
    let source =
        """def noisy: () -> [Console] seq(int) = {
  seq {
    println("one")
    yield 1
    println("two")
    yield 2
    println("three")
    yield 3
  }
}

def paired: [Console] () -> list((int, string)) = {
  seq.zip(noisy(), list.toSeq(["a", "b"])) \seq.toList
}

@entry
def main = {
  println(paired())
}
"""
    // The third element of `noisy` is pulled, and found to have no partner, so "three" is printed.
    runSource (source.ReplaceLineEndings "\n") |> assertRan "one\ntwo\nthree\n[(1, a), (2, b)]\n"

[<Fact>]
let ``Run - set.toSeq and map.toSeq give toList's elements in its order, and work in a for loop`` () =
    let source =
        """@entry
def main = {
  def s = set.fromList([3, 1, 3, 2])
  println(s \set.toSeq \seq.toList)
  println(s \set.toList)
  def m = ["b" => 2, "a" => 1, "b" => 3]
  println(m \map.toSeq \seq.toList)
  println(m \map.toList)
  for set.toSeq(s) | x -> println(x)
  for map.toSeq(m) | entry -> println("{entry.0}={entry.1}")
  def empty: set(int) = []
  println(empty \set.toSeq \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[1, 2, 3]\n[1, 2, 3]\n[(b, 3), (a, 1)]\n[(b, 3), (a, 1)]\n1\n2\n3\nb=3\na=1\n[]\n"
