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
