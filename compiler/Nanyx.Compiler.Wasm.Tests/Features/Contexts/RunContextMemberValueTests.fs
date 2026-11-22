module TranspilerWasmTests.Features.Contexts.RunContextMemberValue

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a context member passed as a value to a function, in a hole`` () =
    // #925
    let source =
        """context ToText(a) = (textlen: a -> int)

def int.textlen: int -> int = { n -> n * 2 }

def apply: [c] ([c] int -> int), int -> int = { f, x -> f(x) }

def viaApply: [ToText(int)] int -> string = { n -> "{apply(ToText(int).textlen, n)}" }

@entry
def main = {
  println(viaApply(21))
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "42\n"

[<Fact>]
let ``Run - a context member kept in a local def and called later`` () =
    // #925: the value had no type, and the call through it trapped.
    let source =
        """context ToText(a) = (textlen: a -> int)

def int.textlen: int -> int = { n -> n * 2 }

def describe: [ToText(a)] a -> string = { value ->
  def g = ToText(a).textlen
  "{g(value)}"
}

@entry
def main = {
  println(describe(5))
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "10\n"

[<Fact>]
let ``Run - a plain context's member named as a value calls the instance where it is called`` () =
    let source =
        """context Holder = (get: () -> int)

def wrap: [Holder] () -> ([Holder] () -> int) = { get }

def kept = {
  use Holder(get = { 1 })
  wrap()
}

@entry
def main = {
  def f = kept()
  use Holder(get = { 2 })
  println(f())
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "2\n"

[<Fact>]
let ``Run - a parameterised context's member named as a value calls the instance where it is called`` () =
    let source =
        """context ToText(a) = (textlen: a -> int)

def kept: [ToText(int)] () -> ([ToText(int)] int -> int) = { ToText(int).textlen }

def made = {
  use ToText(int)(textlen = { n -> n + 1 })
  kept()
}

@entry
def main = {
  def f = made()
  use ToText(int)(textlen = { n -> n * 10 })
  println(f(4))
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "40\n"
