/// `Typed(a)` (see `TypeValues`): the compiler gives an instance for every type, whose `typeOf` is `type a`, so a
/// function given a value of `a` can pass its type on as a type argument.
module TranspilerWasmTests.Features.Contexts.RunTypedContext

open Xunit
open TranspilerWasmTestHelpers

let private show =
    """context Show(a) = (show: a -> string)

def int.show: int -> string = { n -> "int {n}" }

def showAt: [Show(a)] a, Type(a) -> string = { value, t -> show(value) }

def encode: [Typed(a), Show(a)] a -> string = { value -> showAt(value, typeOf) }
"""

[<Fact>]
let ``Run - Typed is given for a record and a built-in type, and its typeOf is passed as a type argument`` () =
    let source =
        show
        + """
type Point = (x: int, y: int)

def Point.show: Point -> string = { p -> "point {p.x}, {p.y}" }

println(encode(Point(x = 1, y = 2)))
println(encode(5))
"""
    runSource source |> assertRan "point 1, 2\nint 5\n"

[<Fact>]
let ``Run - a generic function passes Typed of its own type variable on`` () =
    let source =
        show
        + """
def twice: [Typed(a), Show(a)] a -> string = { value -> encode(value) ++ " / " ++ encode(value) }

println(twice(7))
"""
    runSource source |> assertRan "int 7 / int 7\n"

[<Fact>]
let ``Run - a use loads an instance of Typed, which can only stand for its own type`` () =
    let source =
        show
        + """
def shown = { () ->
  use Typed(int)(typeOf = type int)
  encode(3)
}

println(shown())
"""
    runSource source |> assertRan "int 3\n"

[<Fact>]
let ``Run - Typed is given for a nominal type of another module`` () =
    let people =
        """
module people

export type Person = protected (name: string)

export def Person.new: string -> Person = { name -> Person(name = name) }

export def Person.name: Person -> string = { p -> p.name }
"""
    let main =
        """
module main

import people as p

context Show(a) = (show: a -> string)

def showAt: [Show(a)] a, Type(a) -> string = { value, t -> show(value) }

def encode: [Typed(a), Show(a)] a -> string = { value -> showAt(value, typeOf) }

@entry
def main = {
  use Show(p.Person)(show = { person -> "person {person \p.Person.name}" })
  println(encode(p.Person.new("Ada")))
}
"""
    let outcome, _ = nanyxOnPackage [ "people.nyx", people; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "person Ada\n" outcome
