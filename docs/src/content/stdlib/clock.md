---
title: clock
description: The time of day, and a clock for timing, from the Clock context a host provides
order: 12.65
---

A function that reads the time requires `Clock`. The console and HTTP hosts provide one that reads the operating
system's clocks, and their preludes bring `Clock` into every module of a program. `now()` is the time of day, in
milliseconds since the start of 1970 (Unix time); `ticks()` counts milliseconds from a start of the host's choosing and
never goes backwards, so the difference between two of its values is how long passed between them:

```nanyx
def start = ticks()
def total = list.sum([1, 2, 3, 4])
def took = ticks() - start
println(total)
println(took >= 0)
-- 10
-- true
```

A `use` of `Clock` gives a function whatever time a test needs. A library that names `Clock` in a type imports it
from `nanyx/clock`:

```nanyx
import nanyx/clock as (Clock)

def stamp: [Clock] string -> string = { text -> "{now()}: {text}" }

use Clock(now = { _ -> 1_700_000_000_000 }, ticks = { _ -> 0 })
println(stamp("started"))
-- 1700000000000: started
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Time: a function that reads the time requires [`Clock`](#clock), and calls its members.

A host provides `Clock` to the program's entry point, as it provides `Console`: the console and
HTTP hosts read the operating system's clocks. A `use` of `Clock(now = ..., ticks = ...)` makes
any other, such as one that gives a test the time it needs.

The hosts' preludes make `now` and `ticks` names every module of a program sees; a library that
names `Clock` imports it with `import nanyx/clock as (Clock)`.

## `Clock`

```nanyx fragment
context Clock = (
  now: () -> int
  ticks: () -> int
)
```

The time. A host supplies one; a `use` of `Clock(...)` makes any other.

- `now`: The time of day, as wall clocks read it: milliseconds since the start of 1970, UTC (Unix
  time). It can jump, forwards or backwards, when the system's clock is set, so use `ticks`
  to time something.
- `ticks`: Milliseconds since a start the host chooses, which never go backwards: the time between two
  calls is how long passed between them. Only differences between its values mean anything.

<!-- End of the generated reference. -->
