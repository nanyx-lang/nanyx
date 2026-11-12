module TranspilerWasmTests.Features.StdLib.RunIgnore

open Xunit
open TranspilerWasmTestHelpers

// Each result is bound with the type `()`, so the program type-checks only if `ignore` gives back
// `()`, and is then matched against the pattern `()` to print it.

[<Fact>]
let ``Run - ignore gives back () for a value of any type`` () =
    let source =
        """import nanyx/console

@entry
def main = {
  def fromInt: () = ignore(42)
  def fromString: () = ignore("text")
  def fromList: () = ignore([1, 2, 3])
  def fromTuple: () = ignore((1.5, true))
  def fromTag: () = ignore(#some(7))
  def fromUnit: () = ignore(())
  def units: list(()) = [fromInt, fromString, fromList, fromTuple, fromTag, fromUnit]
  println("{list.length(units)} units")
  match fromString
    | () -> println("()")
}
"""

    runSource source |> assertRan "6 units\n()\n"

[<Fact>]
let ``Run - ignore at the end of a pipeline throws the pipeline's result away`` () =
    let source =
        """import nanyx/console

def shout: [Console] int -> int = { n ->
  println("{n}!")
  n * 2
}

@entry
def main = {
  [1, 2, 3] \list.map(shout) \ignore
  def result: () = [4, 5] \list.map(shout) \ignore
  match result
    | () -> println("()")
}
"""

    runSource source |> assertRan "1!\n2!\n3!\n4!\n5!\n()\n"

[<Fact>]
let ``Run - ignore can be passed as a function`` () =
    let source =
        """@entry
def main = {
  def units: list(()) = ["a", "b"] \list.map(ignore)
  dbg(list.length(units))
}
"""

    runSource source |> assertRan "dbg: 2\n"
