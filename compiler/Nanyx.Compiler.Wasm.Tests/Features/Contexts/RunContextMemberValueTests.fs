module TranspilerWasmTests.Features.Contexts.RunContextMemberValue

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a context member passed as a value to a function, in a hole`` () =
    // #925
    let source =
        """import nanyx/console

context ToText(a) = (textlen: a -> int)

def int.textlen: int -> int = { n -> n * 2 }

def apply: (int -> int), int -> int = { f, x -> f(x) }

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
        """import nanyx/console

context ToText(a) = (textlen: a -> int)

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
