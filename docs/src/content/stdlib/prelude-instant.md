---
title: "prelude: instant"
description: A point in time, its arithmetic with times, and ISO 8601
order: 5.8
---

`Instant` is a point in time, to the nanosecond, which `Clock`'s [`now`](clock) gives. It is in the
[prelude](prelude), so no import is needed. It is moved and measured with [times](prelude-time), and written and read
as ISO 8601 writes it:

```nanyx
match Instant.fromIsoString("2026-11-25T19:20:57.25+01:00")
  | #some(start) ->
    def end = start \plus(90s)
    println(start)
    println(end \since(start))
    println(end \Instant.toIsoDate)
  | _ -> println("not ISO 8601")

-- 2026-11-25T18:20:57.25Z
-- 90s
-- 2026-11-25
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Instant`

```nanyx fragment
type Instant = private int
```

A point in time: a number of nanoseconds since the start of 1970, UTC (Unix time), which
reaches about 292 years either side of it. `Clock`'s `now` gives one. The time between two is
their difference, exactly, and a time added to one moves it: `now() - start` and `now() + 5s`.
[`Instant.since`](#instantsince), [`Instant.plus`](#instantplus) and [`Instant.minus`](#instantminus) are the same by name, and `minus` moves
one back, `now() \minus(5s)`, which `-` doesn't, as it is the time between two. [`Instant.shift`](#instantshift)
moves one by a time that may be negative either way: `now() \shift(-5s)`. It is written as
ISO 8601 writes it, in UTC: `"{i}"` is `2026-11-25T18:20:57Z`.

## `Instant.epoch`

```nanyx fragment
def Instant.epoch: Instant
```

The start of 1970, UTC, which a host's clock counts its time of day from:
`Instant.epoch \plus(1_700_000_000s)`.

## `Instant.since`

```nanyx fragment
def Instant.since: Instant, Instant -> Time(int)
```

The time from `earlier` to `i`, exactly: `now() \since(start)` is how long has passed since
`start`. It is negative if `earlier` is after `i`. It is [`Instant.-`](#instant-) by name.

## `Instant.plus`

```nanyx fragment
def Instant.plus: Instant, Time(int) -> Instant
```

The instant `time` after `i`: `now() \plus(5s)`. It is [`Instant.+`](#instant-1) by name.

## `Instant.minus`

```nanyx fragment
def Instant.minus: Instant, Time(int) -> Instant
```

The instant `time` before `i`: `now() \minus(1d)`. `-` is the time between two instants, so
moving one back is by name only.

## `Instant.shift`

```nanyx fragment
def Instant.shift: Instant, Time(int) -> Instant
```

The instant `time` from `i`: on for a positive time and back for a negative one, as a time
worked out at run time may be either. `now() \shift(-5s)` is five seconds ago.

## `` Instant.`-` ``

```nanyx fragment
def Instant.`-`: Instant, Instant -> Time(int)
```

The time from `earlier` to `i`, exactly, as [`Instant.since`](#instantsince) gives it: `now() - start`.

## `` Instant.`+` ``

```nanyx fragment
def Instant.`+`: Instant, Time(int) -> Instant
```

The instant `time` after `i`, as [`Instant.plus`](#instantplus) gives it: `now() + 5s`.

## `Instant.toUnixTime`

```nanyx fragment
def Instant.toUnixTime: Instant -> Time(int)
```

The time from [`Instant.epoch`](#instantepoch), the start of 1970, UTC, to `at`: its Unix time. Divide it by a
unit for a number of them, as an API that takes Unix time wants: `now() \toUnixTime // 1ms` is
the milliseconds since 1970, and `// 1s` the seconds.

## `Instant.toIsoString`

```nanyx fragment
def Instant.toIsoString: Instant -> string
```

`i` as ISO 8601 writes it, in UTC: its date and its time of day, `2026-11-25T18:20:57Z`, with
the fraction of a second only when it isn't whole, in as few digits as are needed, up to
nanoseconds: `2026-11-25T18:20:57.25Z`. A time before 1970 is written the same way:
`1969-12-31T23:59:59Z`. It is what `"{i}"` writes.

## `Instant.toIsoDate`

```nanyx fragment
def Instant.toIsoDate: Instant -> string
```

The UTC date of `i` alone, as ISO 8601 writes it: `2026-11-25`.

## `Instant.toString`

```nanyx fragment
def Instant.toString: Instant -> string
```

`i` as ISO 8601 writes it, in UTC (see [`Instant.toIsoString`](#instanttoisostring)).

## `` Instant.`==` ``

```nanyx fragment
def Instant.`==`: Instant, Instant -> bool
```

Whether `a` and `b` are the same instant.

## `Instant.compare`

```nanyx fragment
def Instant.compare: Instant, Instant -> #less | #equal | #greater
```

Which of two instants comes first.

## `Instant.hash`

```nanyx fragment
def Instant.hash: Instant -> int
```

The hash of `i`.

## `NotIso`

```nanyx fragment
type NotIso = (at: int, expected: string)
```

Why a text is no ISO 8601 date and time: the byte at which it stops being one, counting from 0,
and what ISO 8601 has there.

## `Instant.fromIsoString`

```nanyx fragment
def Instant.fromIsoString: string -> #some(Instant) | #notIso(NotIso) | #outOfRange
```

The instant `text` writes, as ISO 8601 does: a date and a time of day, `2026-11-25T18:20:57Z`,
with a fraction of a second if it has one, `…57.25Z`, and either `Z`, for UTC, or an offset
from it, `+01:00`, which is taken off to give the time in UTC. A date alone, `2026-11-25`, is
the start of that day in UTC. A year can be written with a sign and up to six digits, as ISO
8601's expanded form writes it, `+012026`.

It gives `#notIso` with the byte at which the text stops being ISO 8601, counting from 0, and
what ISO 8601 has there; and `#outOfRange` for a time more than about 292 years from 1970,
which no `Instant` reaches.

<!-- End of the generated reference. -->
