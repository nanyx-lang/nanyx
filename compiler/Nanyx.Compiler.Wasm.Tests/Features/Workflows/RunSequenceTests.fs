module TranspilerWasmTests.Features.Workflows.RunSequences

open Xunit
open TranspilerWasmTestHelpers

// A lambda with `yield` in it, a block after `seq` included, is a lazy sequence of the values it yields (#1699).

[<Fact>]
let ``Run - a seq block is the sequence of the values it yields`` () =
    let source =
        """def numbers = seq {
  yield 1
  yield 2
}

@entry
def main = {
  println(numbers \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[1, 2]\n"

[<Fact>]
let ``Run - the code after a yield runs only when the sequence is pulled that far`` () =
    let source =
        """def items = seq {
  println("Yielding 1")
  yield 1
  println("Yielding 2")
  yield 2
}

@entry
def main = {
  println("start")
  println(items \seq.take(1) \seq.toList)
  println(items \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "start\nYielding 1\n[1]\nYielding 1\nYielding 2\n[1, 2]\n"

[<Fact>]
let ``Run - a yield in a for loop yields each time round, and the block goes on after the loop`` () =
    let source =
        """def evens = seq {
  for 1..10 | n ->
    if n % 2 == 0 then yield n
}

def around = seq {
  yield 0
  for 1..3 | n ->
    yield n
    yield n * 10
  yield 99
}

@entry
def main = {
  println(evens \seq.toList)
  println(around \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[2, 4, 6, 8, 10]\n[0, 1, 10, 2, 20, 3, 30, 99]\n"

[<Fact>]
let ``Run - a sequence can be recursive`` () =
    let source =
        """def countdown: int -> seq(int) = { n ->
  seq {
    if n > 0 then
      yield n
      for countdown(n - 1) | m -> yield m
  }
}

@entry
def main = {
  println(countdown(3) \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[3, 2, 1]\n"

[<Fact>]
let ``Run - a keyword of a context of the user's own with a #yield member makes a sequence`` () =
    let source =
        """context Gen(a) = (
  @customKeywordType(#yield)
  emit: a -> seq(a)
)

def pairs = seq {
  emit! "a"
  emit! "b"
}

@entry
def main = {
  println(pairs \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[a, b]\n"
