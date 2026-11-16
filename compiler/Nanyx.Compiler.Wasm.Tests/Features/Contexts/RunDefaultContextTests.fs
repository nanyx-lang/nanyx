module TranspilerWasmTests.Features.Contexts.RunDefault

open Xunit
open TranspilerWasmTestHelpers

/// The Contexts page's `valueOrDefault`, which falls back on the `Default` of the value's type.
let private valueOrDefault =
    """
def valueOrDefault: [Default(a)] #some(a) | #none -> a = {
  | #some(v) -> v
  | #none -> default()
}
"""

[<Fact>]
let ``Run - each built-in type has a Default`` () =
    $"""{valueOrDefault}
@entry
def main = {{
  def i: int = valueOrDefault(#none)
  def f: float = valueOrDefault(#none)
  def s: string = valueOrDefault(#none)
  def b: bool = valueOrDefault(#none)
  def xs: list(int) = valueOrDefault(#none)
  def o: #some(int) | #none = valueOrDefault(#none)
  println(i)
  println(f)
  println("[{{s}}]")
  println(b)
  println(xs)
  println(o ?? 7)
  println(valueOrDefault(#some(5)))
}}
"""
    |> normalized
    |> runSource
    |> assertRan "0\n0.0\n[]\nfalse\n[]\n7\n5\n"

[<Fact>]
let ``Run - a type of a user's gets a Default by attaching default`` () =
    $"""{valueOrDefault}
type Point = (x: int, y: int)

def Point.default = {{ -> Point(x = 1, y = 2) }}

@entry
def main = {{
  def p: Point = valueOrDefault(#none)
  println(p.x + p.y)
}}
"""
    |> normalized
    |> runSource
    |> assertRan "3\n"
