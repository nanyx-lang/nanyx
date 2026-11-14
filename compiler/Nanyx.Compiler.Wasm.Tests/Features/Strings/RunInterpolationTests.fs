module TranspilerWasmTests.Features.Strings.RunInterpolation

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a hole of a built-in type writes it as it always has`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def n = -42
  println("{n} {0} {1.5} {true} {false} {"text"} {#red} {n > 0}")
  println("{int.toString(7)} {bool.toString(true)} {float.toString(2.5)}")
}
"""
    runSource source |> assertRan "-42 0 1.5 true false text #red false\n7 true 2.5\n"

[<Fact>]
let ``Run - a hole writes a value through its type's attached toString`` () =
    let source =
        normalized """import nanyx/console

type Point = (x: int, y: int)
def Point.toString = { p -> "({p.x}, {p.y})" }

type Money = private int
def Money.toString: Money -> string = { m ->
  def Money(pence) = m
  "£{pence / 100}.{pence % 100}"
}

@entry
def main = {
  def p = Point(x = 1, y = 2)
  println("p is {p}, costing {Money(250)}")
}
"""
    runSource source |> assertRan "p is (1, 2), costing £2.50\n"

[<Fact>]
let ``Run - a use changes how a hole writes its type`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  println("before {1}")
  def bracketed: ToString(int) = ( toString = { n -> "<{int.toString(n)}>" } )
  use bracketed
  println("after {1}")
}
"""
    runSource source |> assertRan "before 1\nafter <1>\n"

[<Fact>]
let ``Run - a generic function that interpolates its parameter writes each type it is called with`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

type Point = (x: int, y: int)
def Point.toString = { p -> "({p.x}, {p.y})" }

def angled = { value -> "<{value}>" }
def labelled: [ToString(a)] (string, a) -> string = { name, value -> "{name}: {value}" }

@entry
def main = {
  println(angled(1) ++ angled("s") ++ angled(Point(x = 3, y = 4)))
  println(labelled("at", Point(x = 5, y = 6)))
  println([1, 2] \list.map({ n -> "#{n}" }) \string.join(" "))
}
"""
    runSource source |> assertRan "<1><s><(3, 4)>\nat: (5, 6)\n#1 #2\n"

[<Fact>]
let ``Run - a function that requires ToString writes a tag passed to it by its name`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

def angled = { value -> "<{value}>" }
def labelled: [ToString(a)] (string, a) -> string = { name, value -> "{name}: {value}" }
def color: #red | #green = #green

@entry
def main = {
  println(angled(#red) ++ angled(color))
  println(labelled("size", #large))
  println([1, 2] \list.map({ n -> angled(if n == 1 then #one else #two) }) \string.join(" "))
}
"""
    runSource source |> assertRan "<#red><#green>\nsize: #large\n<#one> <#two>\n"

[<Fact>]
let ``Run - a user's own ToString context and the prelude's agree`` () =
    // The program declares a `ToString` of its own, as code written before the prelude had one does.
    let source =
        normalized """import nanyx/console

context ToString(a) = (toString: a -> string)
type Point = (x: int, y: int)
def Point.toString = { p -> "({p.x}, {p.y})" }

def describe: [ToString(a)] = { v -> toString(v) }
def twice = { v -> describe(v) ++ describe(v) }

@entry
def main = {
  println(twice(Point(x = 1, y = 2)))
  println("{Point(x = 3, y = 4)}")
}
"""
    runSource source |> assertRan "(1, 2)(1, 2)\n(3, 4)\n"

[<Fact>]
let ``Run - a hole prints the result of a context's member`` () =
    let source =
        """
import nanyx/console

context Describe(a) = (size: a -> int, label: a -> string, parts: a -> (x: int, y: int))
type Point = (x: int, y: int)

def describe: [Describe(a)] a -> string = { value ->
  "{Describe(a).size(value)} {Describe(a).label(value)} {Describe(a).parts(value)}"
}
def Point.size: Point -> int = { p -> 2 }
def Point.label: Point -> string = { p -> "point" }
def Point.parts: Point -> (x: int, y: int) = { p -> (x = p.x, y = p.y) }

@entry
def main = {
  println(describe(Point(x = 1, y = 2)))
}
"""
        |> _.ReplaceLineEndings("\n")
    runSource source |> assertRan "2 point (x = 1, y = 2)\n"