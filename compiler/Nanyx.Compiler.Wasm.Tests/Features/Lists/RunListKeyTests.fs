module TranspilerWasmTests.Features.Lists.RunKey

open Xunit
open TranspilerWasmTestHelpers

// The list functions that compare elements by a key (distinctBy, minBy, maxBy) and findIndex, the predicate form of
// indexOf.

/// The start of each program: four people, two of each age, and `nameOf`, which names the person a `minBy` or
/// `maxBy` found.
let private withPeople (body: string) =
    """import nanyx/console
import nanyx/text

type Person = (name: string, age: int)

def people = [
  Person(name = "Ann", age = 40),
  Person(name = "Bob", age = 30),
  Person(name = "Cy", age = 40),
  Person(name = "Di", age = 30)
]

def nameOf = {
  | #some(p) -> p.name
  | #emptyList -> "none"
}

def names = { ps -> string.join(list.map(ps, { p -> p.name }), ",") }

"""
    + body.ReplaceLineEndings "\n"

[<Fact>]
let ``Run - list.minBy and list.maxBy give the first element of the least or greatest key`` () =
    let body = """
println(nameOf(people \minBy { p -> p.age }))
println(nameOf(people \maxBy { p -> p.age }))
println(nameOf(list.minBy(people, { p -> p.name })))
println(nameOf(list.maxBy(people, { p -> (p.age, p.name) })))
println(nameOf(list.minBy([], { (p: Person) -> p.age })))
println(nameOf(list.maxBy([], { (p: Person) -> p.age })))
"""
    runSource (withPeople body) |> assertRan "Bob\nAnn\nAnn\nCy\nnone\nnone\n"

[<Fact>]
let ``Run - list.minBy calls a key that requires a context once for each element`` () =
    let body = """
println(nameOf(people \minBy { p ->
  print("{p.name} ")
  p.age
}))
println(nameOf(people \maxBy { p ->
  print("{p.name} ")
  p.age
}))
"""
    runSource (withPeople body) |> assertRan "Ann Bob Cy Di Bob\nAnn Bob Cy Di Ann\n"

[<Fact>]
let ``Run - list.distinctBy keeps the first element of each key, in order`` () =
    let body = """
println(names(people \distinctBy { p -> p.age }))
println(names(list.distinctBy(people, { p -> (p.age, p.name) })))
println(names(list.distinctBy(people, { p -> string.length(p.name) })))
println(names(list.distinctBy([], { (p: Person) -> p.age })))
println(names(people \distinctBy { p ->
  print("{p.age} ")
  p.age
}))
"""
    runSource (withPeople body) |> assertRan "Ann,Bob\nAnn,Bob,Cy,Di\nAnn,Cy\n\n40 30 40 30 Ann,Bob\n"

[<Fact>]
let ``Run - list.findIndex gives the position of the first element a test holds for`` () =
    let body = """
def show = {
  | #some(index) -> "{index}"
  | #notFound -> "not found"
}

println(show(list.findIndex([3, 5, 8, 10], { x -> x % 2 == 0 })))
println(show(people \findIndex { p -> p.age < 40 }))
println(show(list.findIndex([3, 5], { x -> x > 5 })))
println(show(list.findIndex([], { (x: int) -> x > 5 })))
"""
    runSource (withPeople body) |> assertRan "2\n1\nnot found\nnot found\n"
