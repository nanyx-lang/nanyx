---
title: "prelude: time"
description: The Time dimension and its units, from nanoseconds to days
order: 5.7
---

`Time` is how long something takes: a [dimension](../work-in-progress/units-and-dimensions), stored in nanoseconds in
any number type, so `500ms` is a `Time(int)` of 500,000,000 and `0.5s` a `Time(float)`. It and its units are in the
[prelude](prelude), so no import is needed, and [`sleep`](clock) takes a `Time(int)`:

```nanyx
def timeout = 1min + 30s
println(timeout)
println(timeout // 1s)
-- 90s
-- 90
```

| Unit | Is |
| ---- | -- |
| `ns` | a nanosecond, the base unit |
| `us` | a microsecond, `1_000ns` |
| `ms` | a millisecond, `1_000us` |
| `s` | a second, `1_000ms` |
| `min` | a minute, `60s` |
| `hr` | an hour, `60min` |
| `d` | a day, `24hr` |

Months and years are not units of `Time`, as their lengths vary. A module's own `dimension Time` or `unit s` is the
one its name means there, and so is an imported one.
