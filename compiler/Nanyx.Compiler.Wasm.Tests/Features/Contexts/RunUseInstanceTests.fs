module TranspilerWasmTests.Features.Contexts.RunUseInstance

open Xunit
open TranspilerWasmTestHelpers

let private point =
    """
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
type Spot = protected (x: int, y: int)

def shown = use ToString(Spot)(toString = { p -> "<{p.x}, {p.y}>" }) in "{Spot(x = 9, y = 8)}"

println(shown)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "<9, 8>\n"
let private button =
    """
type Button = (label: string, onClick: () -> ())

def ok = Button(label = "ok", onClick = { -> () })
def okToo = Button(label = "ok", onClick = { -> () })
def cancel = Button(label = "cancel", onClick = { -> () })
"""

[<Fact>]
let ``Run - a tag, a tuple and a record holding a type with no Eq of its own compare it through the Eq a use loads`` () =
    let source =
        button
        + """
def main = {
  use Eq(Button)(`==` = { p, q -> p.label == q.label })
  println("{#some(ok) == #some(okToo)} {#some(ok) == #some(cancel)}")
  println("{(ok, 1) == (okToo, 1)} {(ok, 1) == (cancel, 1)}")
  println("{(b = ok, n = 1) == (b = okToo, n = 1)} {(b = ok, n = 1) != (b = cancel, n = 1)}")
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true false\ntrue false\ntrue true\n"

[<Fact>]
let ``Run - a structure holding a function compares it through the Eq of a function type a use loads`` () =
    let source =
        """
def same: [Eq(a)] a, a -> bool = { x, y -> x == y }

def main = {
  use Eq(int -> int)(`==` = { f, g -> f(1) == g(1) })
  def double = { x: int -> x * 2 }
  def twice = { x: int -> x + x }
  def square = { x: int -> x * x }
  println("{#some(double) == #some(twice)} {#some(double) == #some(square)}")
  println("{same((double, 1), (twice, 1))} {same((double, 1), (square, 1))}")
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true false\ntrue false\n"
