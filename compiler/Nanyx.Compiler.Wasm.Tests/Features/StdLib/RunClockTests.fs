module TranspilerWasmTests.Features.StdLib.Clock

open System
open Xunit
open TranspilerWasmTestHelpers

// `nanyx/clock`: the `Clock` context, which the console host provides on WASI's `clock_time_get` and the runtime's
// `nanyx:async` wait, and which a `use` can replace, as a test does.

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
  use Clock(now = { _ -> 1_700_000_000_000 }, ticks = { _ -> 5 }, sleep = { _ -> () })
  println(stamp("hello"))
}
"""
    runSource source |> assertRan "1700000000000 hello after 5\n"

[<Fact>]
let ``Run - the console host's sleep waits at least as long as it is asked`` () =
    let source =
        """def slept: [Async, Clock] int -> int = { ms ->
  def before = ticks()
  sleep(ms)
  ticks() - before
}

println(slept(50) >= 50)
println(slept(0) >= 0)
"""
    runSource source |> assertRan "true\ntrue\n"

[<Fact>]
let ``Run - another task runs while one sleeps`` () =
    let source =
        """def later: [Async, Clock, Console] string -> () = { text ->
  sleep(100)
  println(text)
}

def both = { _ ->
  def first = start! later("the sleeper wakes")
  def second = start! println("another task runs meanwhile")
  await(first)
  await(second)
}

both()
"""
    for inlineCalls in [ true; false ] do
        runSourceWithOptions true inlineCalls "" source |> assertRan "another task runs meanwhile\nthe sleeper wakes\n"

[<Fact>]
let ``Run - a use of Clock gives a sleep that takes no time`` () =
    let source =
        """def nap: [Async, Clock] () -> string = { _ ->
  sleep(3_600_000)
  "woke at {now()}"
}

def faked = { _ ->
  use Clock(now = { _ -> 42 }, ticks = { _ -> 0 }, sleep = { _ -> () })
  nap()
}

println(faked())
"""
    runSource source |> assertRan "woke at 42\n"
