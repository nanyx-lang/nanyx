---
title: clock
description: The time of day, and a clock for timing, from the Clock context a host provides
order: 12.65
---

A function that reads the time requires `Clock`. The console and HTTP hosts provide one that reads the operating
system's clocks, and their preludes bring `Clock` into every module of a program. `now()` is the time of day, an
[`Instant`](prelude-instant); `ticks()` is the time since a start of the host's choosing, a `Time(int)`, and never goes
backwards, so the difference between two of its values is how long passed between them:

```nanyx
def start = ticks()
def total = list.sum([1, 2, 3, 4])
def took = ticks() - start
println(total)
println(took >= 0ns)
-- 10
-- true
```

`sleep(100ms)` waits until at least 100 milliseconds have passed: it takes a `Time(int)`, a number with a unit of time
(`ns`, `us`, `ms`, `s`, `min`, `hr` or `d`), never a plain number. It requires `Async` too, and the program's other
[tasks](../advanced/async) run meanwhile:

```nanyx
def later: [Async, Clock, Console] string -> () = { text ->
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
-- another task runs meanwhile
-- the sleeper wakes
```

A `use` of `Clock` gives a function whatever time a test needs, and a sleep that takes none. A library that names
`Clock` in a type imports it from `nanyx/clock`:

```nanyx
import nanyx/clock as (Clock)

def stamp: [Async, Clock] string -> string = { text ->
  sleep(1min)
  "{now()}: {text}"
}

def stamped = { _ ->
  use Clock(now = { _ -> Instant.epoch \plus(1_700_000_000s) }, ticks = { _ -> 0ns }, sleep = { _ -> () })
  stamp("started")
}

println(stamped())
-- 2023-11-14T22:13:20Z: started
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Time: a function that reads the time, or waits for some to pass, requires [`Clock`](#clock), and calls its
members.

A host provides `Clock` to the program's entry point, as it provides `Console`: the console and
HTTP hosts read the operating system's clocks. A `use` of `Clock(now = ..., ticks = ..., sleep = ...)`
makes any other, such as one that gives a test the time it needs, and sleeps by moving it on.

The hosts' preludes make `now`, `ticks` and `sleep` names every module of a program sees; a library
that names `Clock` imports it with `import nanyx/clock as (Clock)`.

## `Clock`

```nanyx fragment
context Clock = (
  now: () -> Instant
  ticks: () -> Time(int)
  sleep: [Async] Time(int) -> ()
)
```

The time. A host supplies one; a `use` of `Clock(...)` makes any other.

- `now`: The time of day, as wall clocks read it: an `Instant`. It can jump, forwards or
  backwards, when the system's clock is set, so use `ticks` to time something.
- `ticks`: The time since a start the host chooses, which never goes backwards: the time between two
  calls is how long passed between them, `ticks() - start`. Only differences between its values
  mean anything.
- `sleep`: Waits until at least `time` has passed on `ticks`, while the program's other tasks run:
  `sleep(500ms)`. A wait of no time, or less, returns at once.

## `wholeMilliseconds`

```nanyx fragment
def wholeMilliseconds: Time(int) -> int
```

The whole milliseconds a host's `sleep` of `time` waits: `time` rounded up, so a sleep waits
at least as long as it is asked, `500us` a millisecond.

<!-- End of the generated reference. -->
