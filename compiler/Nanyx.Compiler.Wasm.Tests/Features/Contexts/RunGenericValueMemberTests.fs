module TranspilerWasmTests.Features.Contexts.RunGenericValueMember

open Xunit
open TranspilerWasmTestHelpers

// A context member that is a value may be a generic one, as `list.default: list(a)` is: it is the value at the type the
// context is required at (#1794).

/// A context whose member is a value, and a generic type that attaches one.
let private blank =
    """context Blank(a) = (blank: a)

type Pair(a) = protected (items: list(a))

def Pair.blank: Pair(a) = Pair(items = [])

def Pair.size: Pair(a) -> int = { p -> list.length(p.items) }
"""

[<Fact>]
let ``Run - a generic value is a context member at two types in one program`` () =
    let source =
        blank
        + """
def get: [Blank(a)] () -> a = { blank }

def ints: Pair(int) = get()
def strings: Pair(string) = get()

@entry
def main = {
  println(ints \size)
  println(strings \size)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "0\n0\n"

[<Fact>]
let ``Run - a generic value member is passed on by a function generic in the context`` () =
    let source =
        blank
        + """
def fill: [Blank(a)] int -> list(a) = { n -> list.init(n, { _ -> blank }) }

def twice: [Blank(a)] () -> list(a) = { fill(2) }

@entry
def main = {
  def pairs: list(Pair(int)) = twice()
  def names: list(Pair(string)) = fill(3)
  println(list.length(pairs))
  println(list.length(names))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n3\n"

[<Fact>]
let ``Run - the default of a list is the generic value list.default`` () =
    let source =
        """def fallback: [Default(a)] () -> a = { default }

@entry
def main = {
  def xs: list(int) = fallback()
  def names: list(string) = fallback()
  println(xs)
  println(names)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[]\n[]\n"
