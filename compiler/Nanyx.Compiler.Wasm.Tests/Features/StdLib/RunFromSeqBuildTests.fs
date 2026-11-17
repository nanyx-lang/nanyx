module TranspilerWasmTests.Features.StdLib.RunFromSeqBuild

open Xunit
open TranspilerWasmTestHelpers

// Every collection type is made from a sequence with `fromSeq`, and from a `yield` block with `build` (#1906).

/// A program importing `imports`, whose `main` runs `lines`.
let private program (imports: string) (lines: string) =
    $"""{imports}
@entry
def main = {{
  def more = false
{lines}
}}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - list and array fromSeq of an empty, a range and a cut endless sequence, and build`` () =
    let source =
        program
            ""
            """  def none: seq(int) = seq.empty()
  println(none \list.fromSeq)
  println((1..5) \list.fromSeq)
  println(seq.take(0.., 3) \list.fromSeq)
  println(list.build {
    yield 1
    if more then yield 2
  })
  println(none \Array.fromSeq \array.length)
  println((1..5) \Array.fromSeq)
  println(seq.take(0.., 3) \Array.fromSeq)
  println(Array.build {
    yield 1
    yield 2
    if more then yield 3
  })"""
    runSource source |> assertRan "[]\n[1, 2, 3, 4, 5]\n[0, 1, 2]\n[1]\n0\n[1, 2, 3, 4, 5]\n[0, 1, 2]\n[1, 2]\n"

[<Fact>]
let ``Run - set and map fromSeq and build, a later key winning`` () =
    let source =
        program
            ""
            """  def none: seq(int) = seq.empty()
  println(none \set.fromSeq \set.length)
  println((1..5) \set.fromSeq)
  println(seq.take(0.., 3) \set.fromSeq)
  println(set.build {
    yield 1
    yield 1
    if more then yield 2
  })
  println(list.toSeq([("a", 1), ("b", 2), ("a", 3)]) \map.fromSeq)
  def noEntries: seq((string, int)) = seq.empty()
  println(noEntries \map.fromSeq \map.length)
  println(seq.map(seq.take(0.., 2), { n -> (n, n * n) }) \map.fromSeq)
  println(map.build {
    yield ("x", 1)
    if more then yield ("y", 2)
  })"""
    runSource source
    |> assertRan "0\n[1, 2, 3, 4, 5]\n[0, 1, 2]\n[1]\n[a => 3, b => 2]\n0\n[0 => 0, 1 => 1]\n[x => 1]\n"

[<Fact>]
let ``Run - Stack and Queue fromSeq and build`` () =
    let source =
        program
            "import nanyx/stack\nimport nanyx/queue\n"
            """  def none: seq(int) = seq.empty()
  println(none \Stack.fromSeq)
  println((1..3) \Stack.fromSeq)
  println(seq.take(0.., 2) \Stack.fromSeq)
  println(Stack.build {
    yield 1
    if more then yield 2
  })
  println(none \Queue.fromSeq)
  println((1..3) \Queue.fromSeq)
  println(seq.take(0.., 2) \Queue.fromSeq)
  println(Queue.build {
    yield 1
    if more then yield 2
  })"""
    runSource source
    |> assertRan "Stack[]\nStack[1, 2, 3]\nStack[0, 1]\nStack[1]\nQueue[]\nQueue[1, 2, 3]\nQueue[0, 1]\nQueue[1]\n"

[<Fact>]
let ``Run - MList and MArray fromSeq and build`` () =
    let source =
        program
            "import nanyx/mlist\nimport nanyx/marray\n"
            """  memory {
    def none: seq(int) = seq.empty()
    println(none \MList.fromSeq \MList.length)
    println((1..5) \MList.fromSeq \MList.toList)
    println(seq.take(0.., 3) \MList.fromSeq \MList.toList)
    println(MList.build {
      yield 1
      if more then yield 2
    } \MList.toList)
    println(none \MArray.fromSeq \MArray.length)
    println((1..5) \MArray.fromSeq \MArray.toList)
    println(seq.take(0.., 3) \MArray.fromSeq \MArray.toList)
    println(MArray.build {
      yield 1
      if more then yield 2
    } \MArray.toList)
  }"""
    runSource source
    |> assertRan "0\n[1, 2, 3, 4, 5]\n[0, 1, 2]\n[1]\n0\n[1, 2, 3, 4, 5]\n[0, 1, 2]\n[1]\n"

/// A sequence whose steps print, and `made`, a function making a collection from it.
let private noisy (made: string) =
    $"""def noisy: () -> [Console] seq(int) = {{
  seq {{
    println("one")
    yield 1
    println("two")
    yield 2
  }}
}}

{made}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - a collection made from a Console sequence requires Console`` () =
    let source =
        noisy
            """def loud: [Console] () -> array(int) = {
  Array.fromSeq(noisy())
}

@entry
def main = {
  println(loud())
  println(set.fromSeq(noisy()))
}"""
    runSource source |> assertRan "one\ntwo\n[1, 2]\none\ntwo\n[1, 2]\n"

[<Fact>]
let ``Check - a collection made from a Console sequence where Console is not declared is NYX0211`` () =
    let source =
        noisy
            """def quiet: () -> array(int) = {
  Array.fromSeq(noisy())
}"""
    let outcome = checkScript source
    Assert.NotEqual(0, outcome.ExitCode)
    Assert.Contains("error NYX0211: 'array.fromSeq' requires the context 'Console'", outcome.StandardOutput)
