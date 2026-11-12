module TranspilerWasmTests.Features.Operators.RunComposition

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - >> applies the function on its left and then the one on its right, and << the other way round`` () =
    let source =
        """
import nanyx/console

def inc = { n -> n + 1 }
def double = { n -> n * 2 }
def incThenDouble = inc >> double
def doubleThenInc = inc << double
def describe = inc >> double >> { n -> "got {n}" }
@entry
def main = {
  println("{incThenDouble(3)} {doubleThenInc(3)} {describe(1)}")
}
"""
    runSource source |> assertRan "8 7 got 4\n"

[<Fact>]
let ``Run - an operator read as a call takes another read as one, at the top level as in a block`` () =
    // `(2 ** 2) ** 3` at the top level was the call `__pow` with its first operand left as `**`.
    let source =
        """
import nanyx/console

def inc = { n -> n + 1 }
def cube = (2 ** 2) ** 3
def three = (inc >> inc) >> inc
@entry
def main = {
  def local = (2 ** 2) ** 3
  println("{cube} {local} {three(1)}")
}
"""
    runSource source |> assertRan "64 64 4\n"

[<Fact>]
let ``Run - a composition of functions that print prints when it is called`` () =
    let source =
        """
import nanyx/console

def shout = { s -> println(s ++ "!") }
def greet = { name -> "hello " ++ name } >> shout
@entry
def main = {
  println("before")
  greet("ada")
  greet("bob")
}
"""
    runSource source |> assertRan "before\nhello ada!\nhello bob!\n"

[<Fact>]
let ``Run - a spare operator means what a type attaches to it`` () =
    let source =
        """
import nanyx/console

type Money = (pence: int)
def Money.`<>`: Money, Money -> Money = { a, b -> Money(pence = a.pence + b.pence) }
def Money.`^`: Money, int -> Money = { a, times -> Money(pence = a.pence * times) }
@entry
def main = {
  def total = Money(pence = 150) <> Money(pence = 25) ^ 2
  println("{total.pence}")
}
"""
    runSource source |> assertRan "200\n"
