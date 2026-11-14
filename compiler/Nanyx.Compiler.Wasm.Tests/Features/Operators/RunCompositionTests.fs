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

/// Functions that require contexts, and a function that runs one, for the compositions below (#1287).
let private contextParts =
    """
import nanyx/console

context Log = (log: string -> ())

def shout: [Console] string -> () = { s -> println(s ++ "!") }

def tag: [Log] string -> string = { s ->
  log("tagging " ++ s)
  "<" ++ s ++ ">"
}

def applyTo: [c] string, ([c] string -> ()) -> () = { s, f -> f(s) }
"""

[<Fact>]
let ``Run - a composition of functions that require a context takes its members where it is called`` () =
    let source =
        contextParts
        + """
def greet: [Console] string -> () = { name -> "hello " ++ name } >> shout

@entry
def main = {
  greet("ada")
  use Console(
    write = { text -> write("[" ++ text ++ "]") }
    writeError = writeError
    readLine = readLine
    readToEnd = readToEnd
    flush = flush
  )
  greet("bob")
}
"""
    let wat = transpileWat source
    // Building `greet` passes `__composeForward` the two functions, and none of `Console`'s members.
    Assert.Contains("(func $greet (result i32)", wat)
    runSource source |> assertRan "hello ada!\n[hello bob!][\n]"

[<Fact>]
let ``Run - a composition of functions that require different contexts requires them all`` () =
    let source =
        contextParts
        + """
def tagThenShout: [Console & Log] string -> () = tag >> shout

@entry
def main = {
  use Log(log = { m -> println("log: " ++ m) })
  tagThenShout("a")
}
"""
    runSource source |> assertRan "log: tagging a\n<a>!\n"

[<Fact>]
let ``Run - a function that returns a composition leaves its context variable to the composition's caller`` () =
    let source =
        contextParts
        + """
def asked: ([c] string -> ()) -> [c] string -> () = { f -> { s: string -> s ++ "?" } >> f }

@entry
def main = {
  asked(shout)("a")
  def quiet = asked({ s -> () })
  quiet("b")
}
"""
    runSource source |> assertRan "a?!\n"

[<Fact>]
let ``Run - a composition of functions that require a context is passed as an argument`` () =
    let source =
        contextParts
        + """
@entry
def main = {
  applyTo("c", { s: string -> s ++ s } >> shout)
}
"""
    runSource source |> assertRan "cc!\n"
