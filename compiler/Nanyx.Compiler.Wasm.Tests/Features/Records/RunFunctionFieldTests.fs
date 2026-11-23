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

[<Fact>]
let ``Run - a call through a record's field whose function type names its parameters passes them by name`` () =
    // A lambda of several parameters took one WebAssembly argument for each, where a call through a function of a
    // named record passes the record whole, and the call trapped with an indirect call type mismatch (#2466).
    let source =
        normalized """type Client = (ingest: (channel: string, n: int) -> int)

@entry
def main = {
  def c = Client(ingest = { channel, n -> channel.length + n })
  println(c.ingest(channel = "abc", n = 1))
}
"""
    runSource source |> assertRan "4\n"

[<Fact>]
let ``Run - a lambda and a top-level function are values of a function type that names its parameters`` () =
    // Each parameter takes the field of its name, whatever order the call or the function writes them in, and a
    // top-level function's wrapper passes it the fields as its parameters (#2466).
    let source =
        normalized """def apply: ((channel: string, n: int) -> int) -> int = { f -> f(n = 1, channel = "abc") }

def g: (channel: string, n: int) -> int = { channel, n -> channel.length + n }

def h: (n: int, channel: string) -> int = { n, channel -> channel.length * 10 + n }

@entry
def main = {
  def bonus = "!" ++ "!"
  println(apply({ channel, n -> (channel ++ bonus).length + n }))
  println(apply({ n, channel -> channel.length * 100 + n }))
  println(apply(g))
  println(apply(h))
}
"""
    runSource source |> assertRan "6\n301\n4\n31\n"
