module TranspilerWasmTests.Features.Workflows.RunSequenceFor

open Xunit
open TranspilerWasmTestHelpers

// A `for` loop in a sequence's block, `seq { ... }`, walks anything `Iterable`, as a plain
// `for` loop does (#2420).

let private ran (expected: string) (source: string) = runSource (source.ReplaceLineEndings "\n") |> assertRan expected

[<Fact>]
let ``Run - a for loop in a seq block walks a list`` () =
    """def select: [c, d] list(a), ([c] a -> [d] seq(b)) -> list(b) = { xs, f ->
  seq {
    for xs | x ->
      for f(x) | y ->
        yield y
  } \list.fromSeq
}

@entry
def main = {
  println(select([1, 2, 3], { x -> seq[x, x * 10] }))
}
"""
    |> ran "[1, 10, 2, 20, 3, 30]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks an array`` () =
    """@entry
def main = {
  def xs = Array[4, 5]
  println(seq { for xs | x -> yield x * 2 } \list.fromSeq)
}
"""
    |> ran "[8, 10]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks a set`` () =
    """@entry
def main = {
  def xs = set[3, 1, 2, 3]
  println(seq { for xs | x -> yield x } \list.fromSeq \list.sort)
}
"""
    |> ran "[1, 2, 3]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks a map's entries`` () =
    """@entry
def main = {
  def m = ["a" => 1, "b" => 2]
  println(seq { for m | key, value -> yield "{key}={value}" } \list.fromSeq)
}
"""
    |> ran "[a=1, b=2]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks a list lazily`` () =
    """@entry
def main = {
  def xs = [1, 2, 3]
  def s = seq {
    for xs | x ->
      println("at {x}")
      yield x
  }
  println(s \seq.take(1) \seq.toList)
}
"""
    |> ran "at 1\n[1]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks anything a signature says is Iterable`` () =
    """def plusOne: [Iterable(c, elem = int)] c -> list(int) = { xs ->
  seq {
    for xs | x -> yield x + 1
  } \list.fromSeq
}

@entry
def main = {
  println(plusOne(set[1, 2]))
  println(plusOne(Array[5]))
}
"""
    |> ran "[2, 3]\n[6]\n"

[<Fact>]
let ``Run - a for loop in a seq block walks a sequence whose steps need contexts as it is`` () =
    """def doubled: [c] ([c] seq(int)) -> [c] seq(int) = { s ->
  seq {
    for s | x -> yield x * 2
  }
}

@entry
def main = {
  println(doubled(seq[1, 2]) \seq.toList)
}
"""
    |> ran "[2, 4]\n"
