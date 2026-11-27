module TranspilerWasmTests.Features.Memory.SequenceStep

open Xunit
open TranspilerWasmTestHelpers

// What pulling a sequence allocates, per element (#2425; see "What a step costs" on the Lazy sequences deep dive). The
// tests count the blocks the allocator hands out, with memory checks on, for 2,000 elements less those for 1,000, so
// what is made once is left out. A sequence stays persistent: a step that is kept gives the same elements each time it
// is pulled.

let private program =
    normalized """def rangeToList: int -> int = { n ->
  seq.range(1, n) \seq.toList \list.length
}

def yieldToList: int -> int = { n ->
  def s = seq {
    for 1..n | i -> yield i
  }
  s \seq.toList \list.length
}

def yieldTwiceToList: int -> int = { n ->
  def s = seq {
    for 1..n | i ->
      yield i
      yield i
  }
  s \seq.toList \list.length
}

def yieldThenMore: int -> int = { n ->
  def s = seq {
    for 1..n | i -> yield i
    yield 0
  }
  s \seq.toList \list.length
}

def yieldIfFold: int -> int = { n ->
  def s = seq {
    for 1..n | i -> if i % 2 == 0 then yield i * 3 else yield 0
  }
  seq.fold(s, 0, { total, x -> total + x })
}

def mapFold: int -> int = { n ->
  seq.fold(seq.map(1..n, { i -> if i % 2 == 0 then i * 3 else 0 }), 0, { total, x -> total + x })
}

def filterMapFold: int -> int = { n ->
  def evens = seq.filter(1..n, { x -> x % 2 == 0 })
  seq.fold(seq.map(evens, { x -> x * 3 }), 0, { total, x -> total + x })
}
"""

/// How many blocks `name` allocates for each element of the sequence it pulls, and that it gave back `expected`.
let private perElement (name: string) (expected: int -> int) =
    use program = instantiateWasm program []
    // The first call makes what is made once and kept, such as the empty sequence every `seq.empty()` shares.
    program.Call(name, 1) |> ignore
    let liveBefore = program.LiveBlocks
    let forThousand, result = allocationsOf program name 1000
    Assert.Equal(expected 1000, result)
    let forTwoThousand, result = allocationsOf program name 2000
    Assert.Equal(expected 2000, result)
    Assert.Equal(liveBefore, program.LiveBlocks)
    (forTwoThousand - forThousand) / 1000

[<Fact>]
let ``seq.range into seq.toList makes a step's record, closure, tag and payload, and the list's cell, per element`` () =
    Assert.Equal(5, perElement "rangeToList" id)

[<Fact>]
let ``A seq block with a for loop and a yield into seq.toList makes seven blocks per element, as seq.map does`` () =
    // The loop is a seq.map (#2208): four for the range, two for the map's step, one for the list's cell. It was eleven
    // through seq.flatMap and the yield's own sequence.
    Assert.Equal(7, perElement "yieldToList" id)
    // An if whose branches each yield once is a map too, and costs what seq.map does.
    Assert.Equal(perElement "mapFold" (fun n -> 3 * (n / 2) * (n / 2 + 1)), perElement "yieldIfFold" (fun n -> 3 * (n / 2) * (n / 2 + 1)))
    // What follows a loop still runs once the loop's elements are pulled, through seq.append, whose step makes two more.
    Assert.Equal(9, perElement "yieldThenMore" (fun n -> n + 1))

[<Fact>]
let ``A for loop whose body yields twice goes through seq.flatMap`` () =
    // Its body is a sequence of its own: four for the range, two for each of seq.flatMap's steps, four for each yield's
    // sequence and one for each of the list's cells, less what reuse saves: a step's record built from the fields of the
    // one before it is written into its block (#2209).
    Assert.Equal(20, perElement "yieldTwiceToList" (fun n -> 2 * n))

[<Fact>]
let ``seq.filter and seq.map folded make two blocks per stage per element over the range's four`` () =
    // Half the elements pass the filter, so the map's step is made for half of them: 4 + 2 + 1 per element.
    Assert.Equal(7, perElement "filterMapFold" (fun n -> 3 * (n / 2) * (n / 2 + 1)))

[<Fact>]
let ``Run - a sequence step that is kept gives the same elements each time it is pulled`` () =
    let source =
        """@entry
def main = {
  def nested = seq.flatMap(1..3, { n -> 1..n })
  def partway = nested \seq.skip(2)
  println(partway \seq.toList)
  println(partway \seq.toList)
  def yielded = seq {
    for [1, 2, 3] | x ->
      yield x
      yield x * 10
  }
  def rest = yielded \seq.skip(3)
  println(rest \seq.toList)
  println(rest \seq.toList)
  println(yielded \seq.toList)
  def none: seq(seq(int)) = seq { for [1, 2] | _ -> yield seq.empty() }
  println(none \seq.flatten \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[2, 1, 2, 3]\n[2, 1, 2, 3]\n[20, 3, 30]\n[20, 3, 30]\n[1, 10, 2, 20, 3, 30]\n[]\n"

[<Fact>]
let ``Run - seq.flatMap walks past a long run of empty inner sequences without growing the stack`` () =
    let source =
        """@entry
def main = {
  def none: seq(int) = seq.empty()
  println(seq.flatMap(1..200000, { _ -> none }) \seq.toList)
  def late = seq {
    for 1..200000 | i -> if i == 199999 then yield i
  }
  println(late \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[]\n[199999]\n"
