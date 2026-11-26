---
title: "Units of measure"
description: "Numbers with units, checked at compile time: dimensions, units, arithmetic, and points in time"
order: 3.7
---

A number can carry a unit: `500ms`, `2km`, `50MiB`. The compiler checks the units, so a length is never added to a time,
and a function says what it takes: `sleep` takes a time, not "an `int` of milliseconds". The checks cost nothing at
run time: a time is stored as the number it is in nanoseconds, and the units are gone once the program has been
checked.

```nanyx
def timeout = 1min + 30s
println(timeout)
println(timeout / 1s)
println(timeout // 1min)
-- 90s
-- 90.0
-- 1
```

# Dimensions and units

A **dimension** is a kind of quantity: `Time`, `Length`, `Bytes`. A dimension is a type, generic in the number that
stores it, as a `[Num(a)]` function is generic in its number type: `500ms` is a `Time(int)` and `0.5s` a
`Time(float)`.

A **unit** is a named scale within one dimension: `ns`, `ms`, `s`. Units make values, `500ms`, and read them back,
`t / 1s`, but are never part of a type: a function takes a `Time`, not a time in milliseconds.

The prelude declares `Time` and its units, so every module sees them, on every host, as it sees `int`: `ns`, `us`, `ms`,
`s`, `min`, `hr` and `d` (see [prelude: time](../stdlib/prelude-time)). Months and years are not units, as their
lengths vary.

A module declares a dimension of its own with `dimension`, and its units with `unit`:

```nanyx
dimension Length(float)

@baseUnit unit m: Length
unit km: Length = 1_000m
unit mm: Length = 0.001m

dimension Bytes(int)

@baseUnit unit B: Bytes
unit KB: Bytes = 1_000B
unit KiB: Bytes = 1_024B

def walk = 2km + 350m
println(walk)
println(5mm)
println(2KiB)
println(50.5KB)
-- 2350m
-- 5mm
-- 2KiB
-- 50500B
```

- **Dimensions.** `dimension Time` declares a dimension that any number type can store. `dimension Length(float)`
  constrains one to a storage: a `Length` is always a `float`, and a `Bytes` an `int`. The storage must be a number
  type ([NYX0290](../standard-tools/errors#nyx0290)).
- **The base unit.** Each dimension has one unit marked `@baseUnit`, the unit its values are stored in. A dimension with
  none, or with two, is an error ([NYX0286](../standard-tools/errors#nyx0286),
  [NYX0287](../standard-tools/errors#nyx0287)).
- **Other units.** Every other unit is a constant multiple of a unit of the same dimension, so of the base unit in the
  end. The multiple need not be whole, and the base need not be the smallest unit: `mm` is `0.001m`. Units defined in a
  circle, or in another dimension's units, are errors ([NYX0288](../standard-tools/errors#nyx0288),
  [NYX0289](../standard-tools/errors#nyx0289)).
- **Units are names of their own kind**, apart from values and types: `500ms` means the unit `ms` even where a value
  named `ms` is in scope. `dimension` and `unit` are not reserved words either: they begin a declaration only at the
  start of a top-level line, with a name after them.
- **Exporting and importing.** A module exports dimensions and units as it does types, with `export` before the
  declaration, and an import brings in the units its module exports, unqualified, or only those it selects:
  `import physics as (Length, m, km)`. A module's own unit, or an imported one, is the one its name means there, over
  the prelude's. A module can add units to a dimension another declares, `unit ps: Time = 0.001ns`, but not a base unit.

The base unit is what a dimension's values can hold exactly. `Time`'s is `ns`, so a `Time(int)` holds every whole
number of nanoseconds for about 292 years either side of zero, and sums of whole units are exact: `100ms + 200ms ==
300ms`. A `Length(float)` based on `m` holds `5mm` as `0.005`, inexactly, as a `float` holds most decimals.

# Numbers with units

A number followed by a unit with no space between them is one literal: `500ms`, `1_000ns`, `0.5s`, `1.5e3ms`. Its type
is the unit's dimension, and the compiler scales it to the base unit: `0.5s` is stored as `500_000_000`. `100 ms`, with
a space, is the number `100` followed by the name `ms`.

A literal's storage is the one the code around it expects, as a plain number's type is. Where nothing expects one, it
is the constrained dimension's, or else the one the number is written as: `500ms` is a `Time(int)` and `0.5s` a
`Time(float)`.

```nanyx
def t = 0.5s
def u = 500ms
def v: Time(float) = 500ms
def w: Time(int) = 0.5s
println(t)
println(u)
println(v == t)
println(w)
-- 500ms
-- 500ms
-- true
-- 500ms
```

A program doesn't mix two storages by accident, and in an `int` storage a literal must be a whole number of base units:

```nanyx
def t = 0.5s
def u = 500ms
def both = t + u  -- error NYX0201: 'float' vs 'int'
def x: Time(int) = 1.5ns  -- error NYX0201: '1.5ns' is 3/2 of a 'ns', which is no whole number of 'ns', ...
```

A number with no unit is never a measured value, so `sleep(500)` is an error: write `sleep(500ms)`. `zero`, from `Add`,
is the zero of any dimension. A number with a unit is not a pattern: compare with one in a guard, `| == 0ms ->`.

# Dimensions in types

A dimension written without its storage, as `Time`, is a `Time` of any storage, and each one written so is of a storage
of its own. Writing the storage, as `Time(a)`, says two share one:

```nanyx fragment
def isLong: Time -> bool  -- [Num(a)] Time(a) -> bool
def overlap: Time, Time -> bool  -- [Num(a), Num(b)] Time(a), Time(b) -> bool
def total: list(Time(a)) -> Time(a)
```

A signature that names a dimension requires `Num` of its storage without writing it. A storage that only the result
names is an error, since nothing a call passes says what it is ([NYX0293](../standard-tools/errors#nyx0293)): write
`list(Time(a)) -> Time(a)`, not `list(Time) -> Time`. A constrained dimension is written without a storage, `Bytes`,
since it has only one ([NYX0294](../standard-tools/errors#nyx0294)).

# Arithmetic

`+`, `-` and the comparisons take two values of one dimension, and `*` and `/` scale one by a plain number of its
storage. A value divided by another of its dimension is a plain number: how many of the one are in the other. `/` is
fractional division and `//` floored, as on numbers ([Num(a)](./contexts#numa)), so a function that halves a time says
which it means:

| Expression | Type | |
|---|---|---|
| `a + b`, `a - b`, `-a` | `Time(a)` | Both `Time(a)` |
| `a == b`, `a < b`, ... | `bool` | Both `Time(a)` |
| `a * n`, `n * a` | `Time(a)` | Scaling by an `a` |
| `a / n` | `Time(fraction)` | A `Time(int)` divided by `2` is a `Time(float)` |
| `a // n` | `Time(a)` | Floored to a whole number of base units |
| `a / b` | `fraction` | How many `b`s in `a`, with the fraction: `t / 1s` |
| `a // b` | `a` | How many whole `b`s in `a`: `t // 1s` |
| `a % b` | `Time(a)` | What is left: `-1500ms % 1s` is `500ms` |

```nanyx
def half: Time(a) -> Time(fraction) = { t ->
  t / 2
}

def halfDown: Time(a) -> Time(a) = { t ->
  t // 2
}

def total: list(Time(a)) -> Time(a) = { ts ->
  list.sum(ts)
}

println(half(3ns))
println(halfDown(3ns))
println(total([100ms, 200ms, 1s]))
println(-1500ms // 1s)
println(-1500ms % 1s)
println(1s > 999ms)
-- 1.5ns
-- 1ns
-- 1300ms
-- -2
-- 500ms
-- true
```

A dimension has `Add` and `Sub` instances, so generic code such as `list.sum` sums times. It has no `Num` instance:
`Num`'s `*` takes two values of one type, and a `Time` times a `Time` is no `Time` but a `Time * Time` (see [Derived
dimensions](#derived-dimensions)). Two dimensions, or a number with no unit, in a sum are errors:

```nanyx
dimension Length(float)

@baseUnit unit m: Length

def far = 5m + 2s  -- error NYX0201: `+` takes two values of one dimension, and 'Length' and 'Time' are two
```

# Derived dimensions

Dimensions joined by `*` and `/` in a type are a **derived dimension**: `Length / Time` is a speed, `Time * Time` a time
squared, and `Length / (Time * Time)` an acceleration. `dimension Speed = Length / Time` names one, generic in its
storage as its parts are: `Speed(float)` is the same type as `Length(float) / Time(float)`. A storage is written on each
part, `Length(float) / Time(float)`, and the parts share it; a part written without one takes the others'.

`*` and `/` between measured values multiply and divide their dimensions, and dimensions that cancel leave a plain
number. A quotient is of the storage's `fraction`, so dividing two `int`-stored values is not rounded, and `//` rounds
only within one dimension or by a number. There is no syntax for a power yet: a square is `t * t`.

```nanyx
dimension Length

@baseUnit unit m: Length
unit km: Length = 1_000m

dimension Speed = Length / Time

def speed: Length(float), Time(float) -> Speed(float) = { d, t ->
  d / t
}

def sprint = speed(100.0m, 9.58s)
println(sprint * 9.58s)
println(2.0m * 3.0m)
println(1.0km / 1.0m)
-- 100m
-- 6.0m*m
-- 1000.0
```

Two storages on its parts, and a part that is no dimension, are errors
([NYX0295](../standard-tools/errors#nyx0295), [NYX0296](../standard-tools/errors#nyx0296)).

## Units of a derived dimension

A derived dimension's units are defined from its parts' units, multiplied and divided, on one line and read from the
left: `unit kmh: Speed = 1km / 1hr`, or `unit g: Acceleration = 9.80665m / 1s / 1s`. It has no base unit of its own,
since its values are stored in its parts' base units, so a unit of one marked `@baseUnit`, or not defined, is an error
([NYX0297](../standard-tools/errors#nyx0297)), as is a definition of another dimension
([NYX0289](../standard-tools/errors#nyx0289)). A unit can be defined from another of the dimension's units, its own or
imported: `unit mph: Speed = 1.609344kmh`.

A number with one is a value of the dimension, `90.0kmh`, stored as its number of the parts' base units, as `500ms` is.
That is seldom a whole number, a kilometre an hour being 1/3_600_000_000 of a metre a nanosecond, so write it with a
decimal point to store it as a `float`: `90kmh` in an `int` is an error that says so.

A value prints in the largest of the dimension's units it is a whole number of, as a `Time` does, and in its parts' base
units, joined by `*` and `/`, when none fits:

```nanyx
dimension Length

@baseUnit unit m: Length
unit km: Length = 1_000m

dimension Speed = Length / Time

unit kmh: Speed = 1km / 1hr

def cruise = 90.0kmh
println(cruise)
println(36.0km / 0.5hr)
println(cruise + 100.0m / 1.0hr)
-- 90kmh
-- 72kmh
-- 2.5027777777777776e-8m/ns
```

With `unit mps: Speed = 1m / 1s` seen too, `90.0kmh` prints as `25mps`, the larger unit. A `float` that arithmetic has
left within a millionth of a millionth of a whole number of a unit counts as that whole number: `90.0kmh + 10.0mps`
prints `35mps`. A unit defined as nothing, `0m`, or divided by nothing, `1m / 0s`, is an error
([NYX0298](../standard-tools/errors#nyx0298)).

# Printing

A measured value prints in the largest unit in which it is a whole number, and in the base unit otherwise, or when it is
zero:

```nanyx
println("{1.5s} {120s} {16.5ms} {0.25ns} {0s}")
println([1s, 2min, 3hr])
-- 1500ms 2min 16500us 0.25ns 0ns
-- [1s, 2min, 3hr]
```

Only the units the printing module can write are tried: its own, and those it imports. A generic `Time(a)` is printed
in the base unit, `1500000000ns`, and needs `ToString(a)`; a function that wants the largest unit takes a `Time(int)`.
`dbg` writes a measured value as its dimension around its number of base units, `Time(1500000000)`.

# Time in the standard library

`Clock`'s [`sleep`](../stdlib/clock) takes a `Time(int)`, `sleep(500ms)`, and waits at least that long, and `ticks()` is
the time since a start the host chooses, so the difference between two is a `Time`:

```nanyx
def took: [Clock] () -> Time(int) = { _ ->
  def start = ticks()
  ignore(list.sum([1, 2, 3]))
  ticks() - start
}

println(took() >= 0ns)
-- true
```

An HTTP request's `timeout` is a `Time(int)` too: `Request.of(#get, url) with timeout = 5s` (see
[request](../stdlib/request)).

# Instant

`Instant` is a point in time: an `int` of nanoseconds since the start of 1970, UTC. `Clock`'s `now()` gives one. An
instant is moved and measured with times, by the operators and functions attached to it, and written and read as ISO
8601 writes it (see [prelude: instant](../stdlib/prelude-instant)):

| Expression | Type |
|---|---|
| `i \since(j)` | `Time(int)`: the time from `j` to `i`, exactly |
| `i - j` | `Time(int)`: the same, as an operator |
| `i \plus(t)`, `i + t` | `Instant`: `i` moved on by a `Time(int)` `t` |
| `i \minus(t)` | `Instant`: `i` moved back by `t` |
| `Instant.epoch` | `Instant`: the start of 1970 |

```nanyx
match Instant.fromIsoString("2026-11-25T19:20:57.25+01:00")
  | #some(start) ->
    def later = start + 90s
    println(start)
    println(later - start)
    println(later \Instant.toIsoDate)
  | _ -> println("not ISO 8601")

-- 2026-11-25T18:20:57.25Z
-- 90s
-- 2026-11-25
```

An instant prints as its UTC date and time, with a fraction of a second only when it isn't whole. `-` between two
instants is the time between them, so an instant is moved back by name, `i \minus(t)`: a type attaches one `-`, and
`i - 5s` is an error. `5s + i`, with the time on the left, is one too, as the operator is the one its left operand's type
has.
