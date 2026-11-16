/// A call of a function a record's field holds, `counter.add(1, 2)` (#1513).
module TranspilerWasmTests.Features.Records.RunFunctionFields

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a call through a record's function field calls the function it holds`` () =
    let source =
        normalized """type Counter = (add: (int, int) -> int, name: () -> string, twice: int -> int)

def make: int -> Counter = { base ->
  (add = { x, y -> base + x + y }, name = { "counter {base}" }, twice = { n -> n * 2 })
}

def describe: Counter -> string = { c ->
  def total = c.add(1 + 2, c.twice(4))
  "{c.name()}: {total}"
}

println(describe(make(10)))
"""
    runSource source |> assertRan "counter 10: 21\n"
