module TranspilerWasmTests.Features.StdLib.RunListSelect

open Xunit
open TranspilerWasmTestHelpers

// `list.select` and `list.selectOne` run a function with the prelude's `Yield(b)`, whose `yield` adds a value to the
// list they give back (#478).

[<Fact>]
let ``Run - list.select gives the values the function yields, in order`` () =
    let source =
        """type Person = (name: string, isActive: bool)

def names: list(Person) -> list(string) = { people ->
  people \list.select { person ->
    if person.isActive then yield(person.name)
  }
}

def items = [#other(1), #thing, #other(3)]

@entry
def main = {
  println(names([Person(name = "Ada", isActive = true), Person(name = "Bob", isActive = false), Person(name = "Cy", isActive = true)]))
  println(items \list.select { item -> if item is #other(v) then yield(v) })
  println([1, 2] \list.select { x ->
    yield(x)
    yield(x * 10)
  })
  println([1, 2] \list.select { _ -> () })
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[Ada, Cy]\n[1, 3]\n[1, 10, 2, 20]\n[]\n"

[<Fact>]
let ``Run - list.selectOne gives the first value the function yields`` () =
    let source =
        """def items = [#thing, #other(1), #other(3)]

@entry
def main = {
  println(items \list.selectOne { item -> if item is #other(v) then yield(v) })
  println([1, 2] \list.selectOne { x -> if x > 5 then yield(x) })
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(1)\n#notFound\n"

[<Fact>]
let ``Run - the function list.select runs can use the caller's contexts`` () =
    let source =
        """@entry
def main = {
  def doubled = [1, 2] \list.select { x ->
    println("at {x}")
    yield(x * 2)
  }
  println(doubled)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "at 1\nat 2\n[2, 4]\n"

[<Fact>]
let ``Run - a generic function loads an instance at its own type variable for the lambda it is passed`` () =
    let source =
        """context Collect(b) = (collect: b -> ())

def gather: [c] list(a), ([Collect(b), c] a -> ()) -> list(b) = { xs, f ->
  memory {
    mut found = []
    use Collect(b)(collect = { x -> found := [x, ...found] })
    list.fold(xs, (), { _, x -> f(x) })
    list.reverse(found)
  }
}

println(gather([1, 2], { x -> collect("{x}!") }))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[1!, 2!]\n"