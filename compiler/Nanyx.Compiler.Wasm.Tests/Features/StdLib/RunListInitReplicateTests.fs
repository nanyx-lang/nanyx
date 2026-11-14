module TranspilerWasmTests.Features.StdLib.RunListInitReplicate

open Xunit
open TranspilerWasmTestHelpers

// `list.init` and `list.replicate` build a list of a given length (#1261).

[<Fact>]
let ``Run - list.init applies f to each position in order`` () =
    let source =
        """import nanyx/console

@entry
def main = {
  println(list.init(3, { i -> i * 2 }))
  println(list.init(0, { i -> i }))
  println(list.init(-2, { i -> i }))
  println(list.init(1, { i -> "x{i}" }))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[0, 2, 4]\n[]\n[]\n[x0]\n"

[<Fact>]
let ``Run - list.init calls f on the positions in order`` () =
    let source =
        """import nanyx/console

@entry
def main = {
  def xs = list.init(3, { i ->
    println("at {i}")
    i
  })
  println(xs)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "at 0\nat 1\nat 2\n[0, 1, 2]\n"

[<Fact>]
let ``Run - list.replicate gives count copies`` () =
    let source =
        """import nanyx/console

@entry
def main = {
  println(list.replicate(0, count = 3))
  println(list.replicate("a", count = 0))
  println(list.replicate("a", count = -1))
  println(list.replicate("a", count = 1))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[0, 0, 0]\n[]\n[]\n[a]\n"

[<Fact>]
let ``Run - list.init and list.replicate work for a count of 1,000,000`` () =
    let source =
        """import nanyx/console

@entry
def main = {
  def xs = list.init(1000000, { i -> i })
  println("{list.length(xs)} {list.last(xs)}")
  def ys = list.replicate(7, count = 1000000)
  println("{list.length(ys)} {list.sum(ys)}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1000000 #some(999999)\n1000000 7000000\n"
