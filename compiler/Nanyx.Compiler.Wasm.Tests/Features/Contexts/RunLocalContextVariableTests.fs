/// A local function that declares a context variable of the def around it, as `[c]` in `def go: [c] int -> int` inside
/// a `[c]` def, runs in the contexts the variable stands for where the def around it is called (#2465).
module TranspilerWasmTests.Features.Contexts.LocalContextVariables

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an annotated local rec def calls a parameter that requires a context`` () =
    let source = """context Source = (next: () -> #some(int) | #none)

def retry: [c] int, ([c] () -> #some(a) | r) -> #some(a) | r = { times, operation ->
  rec attempt: [c] int -> #some(a) | r = { n ->
    match operation()
      | #some(value) -> #some(value)
      | e -> if n >= times then e else attempt(n + 1)
  }

  attempt(1)
}

use Source(next = { #some(7) }) in println(retry(3, { next() }))
"""
    runSource source |> assertRan "#some(7)\n"

[<Fact>]
let ``Run - an annotated local def calls a parameter that requires a context, and one that requires none`` () =
    let source = """context Source = (next: () -> int)

def plusOne: [c] ([c] () -> int) -> int = { op ->
  def go: [c] int -> int = { n -> op() + n }

  go(1)
}

use Source(next = { 7 }) in println(plusOne({ next() }))
println(plusOne({ 3 }))
"""
    runSource source |> assertRan "8\n4\n"
