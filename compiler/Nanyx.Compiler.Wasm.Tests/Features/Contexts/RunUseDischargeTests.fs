module TranspilerWasmTests.Features.Contexts.RunUseDischarge

open Xunit
open TranspilerWasmTestHelpers

// A member a `use` gives a lambda that needs more contexts runs with the instances in scope at the `use` (#1054).

let private log =
    """
context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}
"""

[<Fact>]
let ``Run - a use's member prints through the Console in scope where the use is written`` () =
    let source =
        log
        + """
@entry
def main = {
  use Log(
    log = { message ->
      println("")
      println(message)
    }
  )
  greet("world")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "\nhello world\n"

[<Fact>]
let ``Run - a use's member calls the outer instance of the context it gives`` () =
    let source =
        log
        + """
def tagged: [Log] () -> () = {
  use Log(log = { m -> log("[app] {m}") })
  greet("world")
}

@entry
def main = {
  use Log(log = { m -> println(m) })
  tagged()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[app] hello world\n"


let private choose =
    """
context Choose = (choose: () -> bool)
context Move = (move: int -> int)

def chooser: [Choose] int -> int = { n -> if choose() then n else 1 }
"""

[<Fact>]
let ``Run - a use's member given by its name runs with the instance in scope where the use is written`` () =
    let source =
        choose
        + """
@entry
def main: [Console] () -> () = {
  use Choose(choose = { true })
  use Move(move = chooser)
  println("{move(5)}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "5\n"

[<Fact>]
let ``Run - a use's member given by its name runs with the instance the def around it requires`` () =
    let source =
        choose
        + """
def go: [Choose & Console] () -> () = {
  use Move(move = chooser)
  println("{move(7)}")
}

@entry
def main: [Console] () -> () = {
  use Choose(choose = { false })
  go()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1\n"