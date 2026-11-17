module TranspilerWasmTests.Features.StdLib.Random

open Xunit
open TranspilerWasmTestHelpers

// `nanyx/random`: the `Random` context, the functions drawn on it, and `random.seeded`, which runs a function under a
// `Random` a seed decides. The seeded tests pin the numbers, so a change to the generator or to how a function draws
// shows here.

[<Fact>]
let ``Run - a seeded Random gives SplitMix64's bits`` () =
    let source = """
import nanyx/random as random
import nanyx/random as (Random)

@entry
def main = {
  random.seeded(0, {
    println(bits())
    println(bits())
  })
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "-2152535657050944081\n7960286522194355700\n"

[<Fact>]
let ``Run - the same seed gives the same numbers`` () =
    let source = """
import nanyx/random as random

def draw = { _ ->
  (
    random.between(1, 6)
    random.below(100)
    random.coinToss()
    random.fraction()
    random.choose(["a", "b", "c"])
  )
}

@entry
def main = {
  def first = random.seeded(42, draw)
  def second = random.seeded(42, draw)
  println(first == second)
  println(first)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true\n(1, 45, false, 0.34419071652363753, #some(c))\n"

[<Fact>]
let ``Run - shuffle gives every element once, in an order the seed decides`` () =
    // The program doesn't import `nanyx/marray`, whose `MArray(xs)` patterns `shuffle` reaches: the backend still
    // knows `MArray` is nominal.
    let source = """
import nanyx/random as random

@entry
def main = {
  def shuffled = random.seeded(7, { random.shuffle([1, 2, 3, 4, 5, 6]) })
  println(shuffled)
  println(list.sort(shuffled))
  println(random.seeded(7, { random.shuffle([]) }) == [])
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[2, 4, 1, 5, 6, 3]\n[1, 2, 3, 4, 5, 6]\ntrue\n"

[<Fact>]
let ``Run - between gives numbers from its low to its high, both included`` () =
    let source = """
import nanyx/random as random

@entry
def main = {
  def rolls = random.seeded(1, { list.init(1000, { _ -> random.between(1, 6) }) })
  println(list.all(rolls, { r -> r >= 1 and r <= 6 }))
  println(list.any(rolls, { r -> r == 1 }) and list.any(rolls, { r -> r == 6 }))
  println(random.seeded(1, { random.between(3, 3) }))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true\ntrue\n3\n"

[<Fact>]
let ``Run - a Random made with use gives the bits it is written to`` () =
    let source = """
import nanyx/random as random
import nanyx/random as (Random)

@entry
def main = {
  use Random(bits = { _ -> -12_346 })
  println(random.coinToss())
  println(random.below(10))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true\n5\n"

[<Fact>]
let ``Run - below a count under 1 stops the program`` () =
    let source = """
import nanyx/random as random

@entry
def main = {
  println(random.seeded(1, { random.below(0) }))
}
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("crash: random.below needs a count of at least 1, and was given 0\n", outcome.StandardError)

[<Fact>]
let ``Run - the console host gives the entry point a Random from the operating system`` () =
    let source = """
import nanyx/random as random

@entry
def main = {
  def rolls = list.init(20, { _ -> random.between(1, 6) })
  println(list.all(rolls, { r -> r >= 1 and r <= 6 }))
  println(random.fraction() < 1.0)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true\ntrue\n"
