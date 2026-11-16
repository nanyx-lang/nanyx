/// A lambda that names the instance of a context it is given, `context l`, and loads it again with `use l` (#1445).
module TranspilerWasmTests.Features.Contexts.ContextNames

open Xunit
open TranspilerWasmTestHelpers

let private log = """import nanyx/console

context Log = (log: string -> ())

def inner: [Log] () -> () = { log("from inner") }
"""

[<Fact>]
let ``Run - a def shadows its Log with a use, then loads its own again with use l`` () =
    let source =
        log
        + """
def run: [Log] () -> () = { context l ->
  use Log(log = { m -> log("[shadowed] {m}") })
  inner()
  use l
  inner()
}

def main = {
  use Log(log = { m -> println(m) })
  run()
}

main()
"""
    runSource source |> assertRan "[shadowed] from inner\nfrom inner\n"

[<Fact>]
let ``Run - use l in, and an inner lambda that captures l to use it`` () =
    let source =
        log
        + """
def run: [Log] () -> () = { context l ->
  use Log(log = { m -> log("[shadowed] {m}") })
  def again = { use l in inner() }
  again()
  use l in log("direct")
}

def main = {
  use Log(log = { m -> println(m) })
  run()
}

main()
"""
    runSource source |> assertRan "from inner\ndirect\n"

[<Fact>]
let ``Run - a lambda passed to a handler names the contexts the handler gives it`` () =
    let source =
        log
        + """
context Tick = (tick: () -> ())

def withBoth: [Console & c] ([Log & Tick & c] int -> ()) -> () = { f ->
  use Log(log = { m -> println("handler: {m}") })
  use Tick(tick = { println("tick") })
  f(1)
}

def main = {
  withBoth { context t: Tick, context g: Log, n ->
    use Log(log = { m -> () })
    use g
    inner()
    use t in tick()
  }
}

main()
"""
    runSource source |> assertRan "handler: from inner\ntick\n"
