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
let ``Run - a value made with a pure lambda where its type carries contexts is pulled with them`` () =
    // #1725: the pure lambda is adapted to the field's type, which takes `Console`'s members.
    let source =
        """type Thunk(a) = [k] protected (get: [k] () -> a)

def run: [Console] ([Console] Thunk(int)) -> int = { t -> t.get() }

def p: [Console] Thunk(int) = Thunk(get = { 1 })
def mk: () -> [Console] Thunk(int) = { Thunk(get = { 2 }) }

println(run(p))
println(run(mk()))
"""
    runSource (normalized source) |> assertRan "1\n2\n"

[<Fact>]
let ``Run - a function returns a value made with a lambda that requires its contexts`` () =
    // #1676: the lambda's `Console` is the value's, required where `get` is called.
    let source =
        thunk
        + """
def mk: int -> [Console] Thunk(int) = { n ->
  Thunk(get = {
    println("x")
    n
  })
}

def t = mk(1)
println(t.get())
"""
    runSource (normalized source) |> assertRan "x\n1\n"

[<Fact>]
let ``Run - a function returns a recursive value built from its argument with lambdas that require its contexts`` () =
    let source =
        """type Stream(a) = [k] protected (next: [k] () -> #next(a, Stream(a)) | #done)

def countdown: int -> [Console] Stream(int) = { n ->
  Stream(next = {
    println("at {n}")
    if n == 0 then #done else #next(n, countdown(n - 1))
  })
}

def drain: [Console] ([Console] Stream(int)) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + drain(rest)
    | #done -> 0
}

println(drain(countdown(3)))
"""
    runSource (normalized source) |> assertRan "at 3\nat 2\nat 1\nat 0\n6\n"

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
[<Fact>]
let ``Run - a generic def's result with fewer contexts than it declares is adapted to them`` () =
    // #1872: `a` is still a variable where `counted([])` is compiled.
    let source =
        thunk
        + """
def lengthOf: list(a) -> Thunk(int) = { xs -> Thunk(get = { list.length(xs) }) }
def counted: list(a) -> [Console] Thunk(int) = { xs -> lengthOf(xs) }

println(force(counted([])))
println(force(counted([1, 2])))
"""
    for inlineCalls in [ true; false ] do
        runSourceWithOptions true inlineCalls "" (normalized source) |> assertRan "0\n2\n"
