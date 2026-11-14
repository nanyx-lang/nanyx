module TranspilerWasmTests.Features.Functions.RunTupleArgument

open Xunit
open TranspilerWasmTestHelpers

// `int, int -> int` and `(int, int) -> int` are one type, so a function value of that type is called the same way
// however its lambda names its parameters and however the call writes its argument (#441).

[<Fact>]
let ``Run - a function value whose parameter is a tuple is called with a tuple (#441)`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def add: (int, int) -> int = { t -> t.0 + t.1 }
  println("{add((3, 4))}")
}
"""
    runSource source |> assertRan "7\n"

[<Fact>]
let ``Run - a function value whose parameter is a tuple pattern is called with a tuple or with two arguments`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def add = { (r, s) -> r + s }
  def pair = (5, 6)
  println("{add((3, 4))} {add(1, 2)} {add(pair)}")
}
"""
    runSource source |> assertRan "7 3 11\n"

[<Fact>]
let ``Run - a lambda returned from another is called with a tuple`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def adder = { n -> { (r, s) -> n + r + s } }
  def add = adder(1)
  println("{add((3, 4))}")
}
"""
    runSource source |> assertRan "8\n"

[<Fact>]
let ``Run - a function value of two parameters is called with a tuple held in a variable`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def add = { r, s -> r + s }
  def t = (3, 4)
  println("{add(t)} {add((1, 2))} {add(5, 6)}")
}
"""
    runSource source |> assertRan "7 3 11\n"

[<Fact>]
let ``Run - a lambda of two parameters is called by the standard library with each tuple of a list`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

def numberGame = { rules ->
  { i ->
    def r = rules \list.map { divisor, word -> if i % divisor == 0 then word else "" } \string.join("")
    match r
      | "" -> "{i}"
      | s -> s
  }
}

@entry
def main = {
  def fizzbuzz = numberGame([(3, "fizz"), (5, "buzz"), (7, "baz")])
  for 1..15 | i -> println(fizzbuzz(i))
}
"""
    runSource source
    |> assertRan "1\n2\nfizz\n4\nbuzz\nfizz\nbaz\n8\nfizz\nbuzz\n11\nfizz\n13\nbaz\nfizzbuzz\n"

[<Fact>]
let ``Run - a top-level function of one tuple parameter is passed as a value and called with two arguments`` () =
    let source =
        normalized """import nanyx/console

def sum: (int, int) -> int = { t -> t.0 + t.1 }
def apply: ((int, int) -> int) -> int = { f -> f(3, 4) }

@entry
def main = {
  println("{apply(sum)} {[(1, 2)] \list.map(sum) \list.sum}")
}
"""
    runSource source |> assertRan "7 3\n"

[<Fact>]
let ``Run - a tuple in brackets of its own is one argument of a pipe (#1002)`` () =
    let source =
        """
import nanyx/console

@entry
def main = {
  def piped = [1, 2, 3] \list.fold((0, 0)) { (sum, count), x -> (sum + x, count + 1) }
  println("{piped.0} {piped.1}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "6 3\n"

[<Fact>]
let ``Run - a record in brackets of its own is one argument of a pipe (#1031)`` () =
    let source =
        """
import nanyx/console

def show = { r: int, opts: (width: int) -> "{r} {opts.width}" }
def three = { r: int, opts: (width: int), s: string -> "{r} {opts.width} {s}" }

println(1 \show((width = 3)))
println(1 \three((width = 4), "x"))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1 3\n1 4 x\n"
