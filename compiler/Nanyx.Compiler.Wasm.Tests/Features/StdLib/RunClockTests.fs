module TranspilerWasmTests.Features.StdLib.Clock

open System
open Xunit
open TranspilerWasmTestHelpers

// `nanyx/clock`: the `Clock` context, which the console host provides on WASI's `clock_time_get` and the runtime's
// `nanyx:async` wait, and which a `use` can replace, as a test does.

[<Fact>]
let ``Run - the console host's Clock gives the time of day as an Instant`` () =
    let before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let outcome = runSource "println(now() \\since(Instant.epoch) // 1ms)\nprintln(now())\n"
    let after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    Assert.Equal("", outcome.StandardError)
    let lines = outcome.StandardOutput.Trim().Split('\n')
    Assert.InRange(Int64.Parse(lines[0]), before - 1000L, after + 1000L)
    let printed = DateTimeOffset.Parse(lines[1], Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()
    Assert.InRange(printed, before - 1000L, after + 1000L)

[<Fact>]
let ``Run - the console host's ticks never go backwards`` () =
    let source =
        """def rising: [Clock] int, Time(int) -> bool = { count, last ->
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
  use Clock(now = { _ -> Instant.epoch \plus(1_700_000_000s) }, ticks = { _ -> 5ms }, sleep = { _ -> () })
  println(stamp("hello"))
}
"""
    runSource source |> assertRan "2023-11-14T22:13:20Z hello after 5ms\n"

[<Fact>]
let ``Run - the console host's sleep waits at least as long as it is asked`` () =
    let source =
        """def slept: [Async, Clock] Time(int) -> Time(int) = { time ->
  def before = ticks()
  sleep(time)
  ticks() - before
}

println(slept(50ms) >= 50ms)
println(slept(0.5s) >= 0.5s)
println(slept(500us) >= 500us)
println(slept(0ns) >= 0ns)
"""
    runSource source |> assertRan "true\ntrue\ntrue\ntrue\n"

[<Fact>]
let ``Run - another task runs while one sleeps`` () =
    let source =
        """def later: [Async, Clock, Console] string -> () = { text ->
  sleep(100ms)
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
  sleep(1hr)
  "woke at {now()}"
}

def faked = { _ ->
  use Clock(now = { _ -> Instant.epoch \plus(42s) }, ticks = { _ -> 0ns }, sleep = { _ -> () })
  nap()
}

println(faked())
"""
    runSource source |> assertRan "woke at 1970-01-01T00:00:42Z\n"
