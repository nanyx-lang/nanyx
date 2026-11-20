module TranspilerWasmTests.Features.StdLib.Clock

open System
open Xunit
open TranspilerWasmTestHelpers

// `nanyx/clock`: the `Clock` context, which the console host provides on WASI's `clock_time_get`, and which a `use`
// can replace, as a test does.

[<Fact>]
let ``Run - the console host's Clock gives the time of day in Unix milliseconds`` () =
    let before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let outcome = runSource "println(now())\n"
    let after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    Assert.Equal("", outcome.StandardError)
    let printed = Int64.Parse(outcome.StandardOutput.Trim())
    Assert.InRange(printed, before - 1000L, after + 1000L)

[<Fact>]
let ``Run - the console host's ticks never go backwards`` () =
    let source =
        """def rising: [Clock] int, int -> bool = { count, last ->
  if count == 0 then true else
    def next = ticks()
    if next < last then false else rising(count - 1, next)
}

println(rising(1000, ticks()))
"""
    runSource source |> assertRan "true\n"

[<Fact>]
let ``Run - a use of Clock gives a function the time a test chooses`` () =
    let source =
        """import nanyx/clock as (Clock)

def stamp: [Clock] string -> string = { text -> "{now()} {text} after {ticks()}" }

@entry
def main = {
  use Clock(now = { _ -> 1_700_000_000_000 }, ticks = { _ -> 5 })
  println(stamp("hello"))
}
"""
    runSource source |> assertRan "1700000000000 hello after 5\n"
