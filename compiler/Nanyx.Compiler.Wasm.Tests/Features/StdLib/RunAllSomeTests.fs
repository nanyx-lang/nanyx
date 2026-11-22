module TranspilerWasmTests.Features.StdLib.RunAllSome

open TranspilerWasmTestHelpers
open Xunit

[<Fact>]
let ``Run - allSome of the empty list is #some of the empty list`` () =
    let source =
        """@entry
def main = {
  def options: list(#some(int) | #none) = []
  println(allSome(options))
}
"""

    runSource source |> assertRan "#some([])\n"

[<Fact>]
let ``Run - allSome gives every value when each element is #some`` () =
    let source =
        """@entry
def main = {
  println(["1", "2", "3"] \list.map(int.fromString) \allSome)
}
"""

    runSource source |> assertRan "#some([1, 2, 3])\n"

[<Fact>]
let ``Run - allSome gives the first element that isn't #some, and not a later one`` () =
    let source =
        """def find: int -> #some(int) | #none | #negative(int) = { n ->
  if n > 0 then #some(n) else if n == 0 then #none else #negative(n)
}

@entry
def main = {
  println(["1", "x", "3", "y"] \list.map(int.fromString) \allSome)
  println([1, 0, -2] \list.map(find) \allSome)
  println([1, -2, 0] \list.map(find) \allSome)
}
"""

    runSource source |> assertRan "#invalidInt\n#none\n#negative(-2)\n"

[<Fact>]
let ``Run - allSome gives the first failure with its payload`` () =
    let source =
        """@entry
def main = {
  def results: list(#some(int) | #failed(string)) = [#some(1), #failed("bad"), #some(3), #failed("worse")]
  def fine: list(#some(int) | #failed(string)) = [#some(1), #some(2)]
  println(allSome(results))
  println(allSome(fine))
}
"""

    runSource source |> assertRan "#failed(bad)\n#some([1, 2])\n"

[<Fact>]
let ``Run - allSome works on 100,000 elements`` () =
    let source =
        """def upTo: int, list(#some(int) | #none) -> list(#some(int) | #none) = { n, acc ->
  if n == 0 then acc else upTo(n - 1, [#some(n), ...acc])
}

@entry
def main = {
  match allSome(upTo(100000, []))
    | #some(values) -> println("{list.length(values)} {list.fold(values, 0, { _, v -> v })}")
    | #none -> println("none")
}
"""

    runSource source |> assertRan "100000 100000\n"
