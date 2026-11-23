/// A lambda checked against a function type that requires a context, as a record field's or a function's result,
/// calls the members of the instance in scope where it is called (#2467).
module TranspilerWasmTests.Features.Contexts.ExpectedFunctionContexts

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a lambda given for a record field whose type requires a context calls its member`` () =
    let source = """context T = (post: string -> int)

type Client = (ingest: [T] string -> int)

def make: () -> Client = {
  Client(ingest = { s -> post(s) + 1 })
}

def main = {
  def c = make()
  use T(post = { s -> s.length }) in println(c.ingest("abc"))
}
"""
    runSource source |> assertRan "4\n"

[<Fact>]
let ``Run - a lambda a function gives back calls the members its result type requires`` () =
    let source = """context T = (post: string -> int)
context Show(a) = (show: a -> string)

def ingestWith: int -> ([T] string -> int) = { n -> { s -> post(s) + n } }

def shower: () -> ([Show(int)] int -> string) = { { n -> show(n) ++ "!" } }

def main = {
  use T(post = { s -> s.length }) in println(ingestWith(2)("abc"))
  use Show(int)(show = { n -> "n{n}" }) in println(shower()(5))
}
"""
    runSource source |> assertRan "5\nn5!\n"
