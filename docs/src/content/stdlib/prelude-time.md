---
title: "prelude: time"
description: The Time dimension and its units, from nanoseconds to days
order: 5.7
---

`Time` is how long something takes: a [dimension](../advanced/units-of-measure), stored in nanoseconds in
any number type, so `500ms` is a `Time(int)` of 500,000,000 and `0.5s` a `Time(float)`. It and its units are in the
[prelude](prelude), so no import is needed, and [`sleep`](clock) takes a `Time(int)`:

```nanyx
def timeout = 1min + 30s
println(timeout)
println(timeout // 1s)
-- 90s
-- 90
```

A module's own `dimension Time` or `unit s` is the one its name means there, and so is an imported one.

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Time`

```nanyx fragment
dimension Time
```

How long something takes: a duration, stored in nanoseconds in any number type, so `500ms` is
a `Time(int)` of 500,000,000 and `0.5s` a `Time(float)`. A point in time is not one.

## `ns`

```nanyx fragment
@baseUnit unit ns: Time
```

A nanosecond, `Time`'s base unit: every other unit of it is a whole number of them.

## `us`

```nanyx fragment
unit us: Time = 1_000ns
```

A microsecond, 1,000 nanoseconds.

## `ms`

```nanyx fragment
unit ms: Time = 1_000us
```

A millisecond, 1,000 microseconds.

## `s`

```nanyx fragment
unit s: Time = 1_000ms
```

A second, 1,000 milliseconds.

## `min`

```nanyx fragment
unit min: Time = 60s
```

A minute, 60 seconds.

## `hr`

```nanyx fragment
unit hr: Time = 60min
```

An hour, 60 minutes.

## `d`

```nanyx fragment
unit d: Time = 24hr
```

A day, 24 hours. Months and years are not units of `Time`, as their lengths vary.

<!-- End of the generated reference. -->
