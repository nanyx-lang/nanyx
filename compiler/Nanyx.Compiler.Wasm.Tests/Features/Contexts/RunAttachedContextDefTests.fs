/// A def attached to a context is in scope by its bare name wherever the context is available, as the context's
/// members are, and can be a `#delay` keyword (#2493, see `AttachedContextDefs`).
module TranspilerWasmTests.Features.Contexts.RunAttachedContextDefs

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A def attached to a context is called by its bare name under a use of the context, and in a def that requires it`` () =
    let source =
        """context Greeter = protected ()

def Greeter.hello: [Greeter] string -> string = { s -> "hello {s}" }

@customKeywordType(#delay)
def Greeter.later: [Greeter] (() -> string) -> string = { work -> "later: {work()}" }

def greet: [Greeter] () -> string = { _ -> hello("from a def") }

def run = { _ ->
  use Greeter()
  println(later! "x")
  println(greet())
  println(["a", "b"] \list.map(hello))
}

run()
"""
    runSource source |> assertRan "later: x\nhello from a def\n[hello a, hello b]\n"
