module TranspilerWasmTests.Features.Contexts.RunUseInstance

open Xunit
open TranspilerWasmTestHelpers

let private point =
    """
import nanyx/console

type Point = (x: int, y: int)

def points = [Point(x = 1, y = 2), Point(x = 3, y = 4)]
"""

[<Fact>]
let ``Run - use C(T)(members) loads the instance it writes out`` () =
    let source =
        point
        + """
def total = {
  use Add(Point)(
    zero = Point(x = 0, y = 0)
    `+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
    `-` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }
    negate = { a -> a }
  )
  points \list.sum
}

def sum = total()

println("{sum.x} {sum.y}")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "4 6\n"

[<Fact>]
let ``Run - use C(T)(members) in body loads the instance for the body`` () =
    let source =
        point
        + """
def shown = use ToString(Point)(toString = { p -> "<{p.x}, {p.y}>" }) in "{Point(x = 9, y = 8)}"

println(shown)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "<9, 8>\n"