/// A local function whose type requires a parameterised context is a function value that carries the context's
/// members, at the types it is called with: each call passes those the type checker found there (#1368).
module TranspilerWasmTests.Features.Contexts.LocalInstanceContexts

open Xunit
open TranspilerWasmTestHelpers

let private showContext = """context Show(a) = (show: a -> string)
type Point = (x: int, y: int)
type Size = (w: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def Size.show: Size -> string = { s -> "size {s.w}" }
def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
"""

[<Fact>]
let ``Run - a local function is passed the instance each call finds, not the def around it's`` () =
    let source = showContext + """def local = { v ->
  def f: [Show(b)] b -> string = { x -> describe(x) }
  f(v) ++ " / " ++ f(Size(w = 3))
}
@entry
def main = { println(local(Point(x = 1, y = 2))) }
"""
    runSource (normalized source) |> assertRan "(1, 2) / size 3\n"

[<Fact>]
let ``Run - a local function that requires a parameterised context, in a def that requires none`` () =
    let source = showContext + """@entry
def main = {
  def f: [Show(a)] a -> string = { x -> describe(x) }
  println(f(Point(x = 1, y = 2)))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\n"

[<Fact>]
let ``Run - a local function that writes its context as the def around it does`` () =
    let source = showContext + """def local: [Show(a)] a -> string = { v ->
  def f: [Show(a)] a -> string = { x -> "[" ++ describe(x) ++ "]" }
  f(v)
}
@entry
def main = {
  println(local(Point(x = 1, y = 2)))
  println(local(Size(w = 4)))
}
"""
    runSource (normalized source) |> assertRan "[(1, 2)]\n[size 4]\n"

[<Fact>]
let ``Run - a local function that requires a context without parameters too`` () =
    let source = showContext + """@entry
def main = {
  def say: [Console & Show(b)] b -> () = { x -> println(describe(x)) }
  say(Point(x = 1, y = 2))
  say(Size(w = 5))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\nsize 5\n"

[<Fact>]
let ``Run - a call to a local function finds an instance a use puts in scope`` () =
    let source = showContext + """def plain: Show(Point) = (show = { p -> "point" })
@entry
def main = {
  def f: [Show(b)] b -> string = { x -> describe(x) }
  println(f(Point(x = 1, y = 2)))
  use plain in println(f(Point(x = 1, y = 2)))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\npoint\n"

[<Fact>]
let ``Run - a local function that requires a context at a type it names`` () =
    let source = showContext + """@entry
def main = {
  def f: [Show(Size)] Size -> string = { x -> "<" ++ describe(x) ++ ">" }
  println(f(Size(w = 6)))
}
"""
    runSource (normalized source) |> assertRan "<size 6>\n"

[<Fact>]
let ``Run - a local function that does not write its type is inferred to require the context (#550)`` () =
    let source = showContext + """def local = { v ->
  def f = { x -> describe(x) }
  f(v)
}
@entry
def main = { println(local(Point(x = 1, y = 2))) }
"""
    runSource (normalized source) |> assertRan "(1, 2)\n"

[<Fact>]
let ``Run - a local function with an inferred context, called at two types in a def that requires none`` () =
    let source = showContext + """@entry
def main = {
  def f = { x -> "<" ++ describe(x) ++ ">" }
  println(f(Point(x = 1, y = 2)) ++ " / " ++ f(Size(w = 3)))
}
"""
    runSource (normalized source) |> assertRan "<(1, 2)> / <size 3>\n"
[<Fact>]
let ``Run - a local function that writes an operator's context is passed its instance at each type`` () =
    let source = """@entry
def main = {
  def f: [Add(a)] a, a -> a = { x, y -> x + y }
  println(f(1, 2))
  println(f(1.5, 2.0))
}
"""
    runSource (normalized source) |> assertRan "3\n3.5\n"