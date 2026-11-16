/// A type generic in contexts, `type Thunk(a) = [k] protected (...)`: a value holds functions that require the contexts
/// its type carries, and is passed their members where they are called (#1525).
module TranspilerWasmTests.Features.Contexts.ContextGenericTypes

open Xunit
open TranspilerWasmTestHelpers

let private thunk = """type Thunk(a) = [k] protected (get: [k] () -> a)

def pure: Thunk(int) = Thunk(get = { 1 })
def loud: [Console] Thunk(int) = Thunk(get = {
  println("got")
  2
})
def force: [c] ([c] Thunk(a)) -> a = { t -> t.get() }
def map: ([c] Thunk(a)), ([d] a -> b) -> [c, d] Thunk(b) = { t, f -> Thunk(get = { f(t.get()) }) }
"""

[<Fact>]
let ``Run - a pure and an effectful value are pulled where their contexts are`` () =
    let source =
        thunk
        + """
def pulled: () -> int = { force(pure) + pure.get() }

println(force(loud) + force(pure))
println(pulled())
println(loud.get())
"""
    runSource (normalized source) |> assertRan "got\n3\n2\ngot\n2\n"

[<Fact>]
let ``Run - a map-like function gives a value that carries both contexts`` () =
    let source =
        thunk
        + """
def mapped = map(loud, { x -> x + 1 })
def labelled = map(pure, { x ->
  println("labelling")
  "{x}!"
})

println(force(mapped))
println(force(labelled))

@entry
def main = {
  def local = Thunk(get = {
    println("local")
    5
  })
  println(force(map(local, { x -> x * 2 })))
}
"""
    runSource (normalized source) |> assertRan "got\n3\nlabelling\n1!\nlocal\n10\n"

[<Fact>]
let ``Run - a value with fewer contexts is given where one with more is expected`` () =
    let source =
        thunk
        + """
def runLoud: [Console] ([Console] Thunk(int)) -> int = { t -> t.get() }
def widenedValue: [Console] Thunk(int) = pure
def widenedResult: () -> [Console] Thunk(int) = { pure }

println(runLoud(pure))
println(runLoud(widenedValue))
println(runLoud(widenedResult()))
"""
    runSource (normalized source) |> assertRan "1\n1\n1\n"