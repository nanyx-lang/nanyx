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
stores it, as a `[Num(a)]` function is generic in its number type: `500ms` and `0.5s` are a `Time(int)` and `0.25ns` a
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
-- 2.35km
-- 5.0mm
-- 2KiB
-- 50500B
```

- **Dimensions.** `dimension Time` declares a dimension that any number type can store. `dimension Length(float)`
  constrains one to a storage: a `Length` is always a `float`, and a `Bytes` an `int`. The storage must be a number
  type ([NYX0290](../standard-tools/errors#nyx0290)).
- **Units.** One unit of a dimension is no multiple of another, `unit m: Length`, and every other unit is a constant
  multiple of a unit of the same dimension, so of that one in the end. The multiple need not be whole, and the first
  unit need not be the smallest: `mm` is `0.001m`. A dimension with no units, or with two that are no multiple of
  another, is an error ([NYX0286](../standard-tools/errors#nyx0286)), and so are units defined in a circle, or in another
  dimension's units ([NYX0288](../standard-tools/errors#nyx0288), [NYX0289](../standard-tools/errors#nyx0289)).
- **The default storage unit.** A dimension's values are stored as a number of its **default storage unit**: the largest
  unit that every unit declared with the dimension is a whole number of. It is usually the smallest, `mm` above, but
  not always: with `mm` and `inch = 25.4mm`, an inch is no whole number of millimetres, so no declared unit is one, and
  the compiler makes one up, `0.2mm`. It has no name, so a value that is a whole number of no unit is written as an exact
  decimal of the smallest unit it is one of, `2.6mm`. `@storageUnit` marks it by hand,
  `@storageUnit unit ns: Time`, so that adding a smaller unit is an error rather than a change to what every value
  holds ([NYX0300](../standard-tools/errors#nyx0300)).
- **The default display unit.** `@baseUnit` marks the unit a dimension is written in when no other unit is chosen, as
  zero is: `0m`. It may be a multiple, as `m` is of `mm`, and is optional: a dimension with no `@baseUnit` is written in
  its default storage unit, or, if that is made up, in the smallest unit it is a decimal of. A dimension has at most one of each mark ([NYX0287](../standard-tools/errors#nyx0287)).
- **Units are names of their own kind**, apart from values and types: `500ms` means the unit `ms` even where a value
  named `ms` is in scope. `dimension` and `unit` are not reserved words either: they begin a declaration only at the
  start of a top-level line, with a name after them.
- **Exporting and importing.** A module exports dimensions and units as it does types, with `export` before the
  declaration, and an import brings in the units its module exports, unqualified, or only those it selects:
  `import physics as (Length, m, km)`. A module's own unit, or an imported one, is the one its name means there, over
  the prelude's. A module can add units to a dimension another declares, `unit tick: Time = 10ns`, but each must be a
  whole number of the dimension's default storage unit, so that it doesn't change what a value holds
  ([NYX0300](../standard-tools/errors#nyx0300)), and it can't mark them ([NYX0287](../standard-tools/errors#nyx0287)).

The default storage unit is what a dimension's values can hold exactly. `Time`'s is `ns`, so a `Time(int)` holds every
whole number of nanoseconds for about 292 years either side of zero, and sums of whole units are exact: `100ms + 200ms
== 300ms`. Every unit declared with a dimension is a whole number of its default storage unit, so a `Length(float)`
holds `5mm` as exactly `5.0`.

# Numbers with units

A number followed by a unit with no space between them is one literal: `500ms`, `1_000ns`, `0.5s`, `1.5e3ms`. Its type
is the unit's dimension, and the compiler scales it to the default storage unit: `0.5s` is stored as `500_000_000`. `100 ms`, with
a space, is the number `100` followed by the name `ms`.

A literal's storage is the one the code around it expects, as a plain number's type is. Where nothing expects one, it
is the constrained dimension's, or else an `int` when the literal is a whole number of default storage units that fits
one, and a `float` when it isn't. How the number is written doesn't matter: `0.5s` and `500ms` are both 500_000_000
nanoseconds, so both are a `Time(int)`, and `0.25ns` is a `Time(float)`. Literals of one storage, as in `2.0 * 1.5s`
or `[0.5s, 0.25ns]`, are a `float` together if one of them must be. A whole number is stored as a `float` where a
`float` is expected: `def v: Time(float) = 500ms`.

```nanyx
def t = 0.5s
def u = 500ms
def v: Time(float) = 500ms
def w = 0.25ns
println(t)
println(u == t)
println(v)
println(w)
-- 500ms
-- true
-- 500.0ms
-- 0.25ns
```

A program doesn't mix two storages by accident, and in an `int` storage a literal must be a whole number of default
storage units:

```nanyx
def t: Time(float) = 0.5s
def u = 500ms
def both = t + u  -- error NYX0201: 'float' vs 'int'
def x: Time(int) = 1.5ns  -- error NYX0201: '1.5ns' is 3/2 of a 'ns', which is no whole number of 'ns', ...
```

A number with no unit is never a measured value, so `sleep(500)` is an error: write `sleep(500ms)`. `zero`, from `Add`,
is the zero of any dimension. A number with a unit is not a pattern: compare with one in a guard, `| == 0ms ->`.

# Dimensions in types

A dimension is written with its storage, as `Time(a)` or `Time(float)`, or without it, as `Time`. Those a function is
passed that are written without one share one storage, and those in its result are of whatever storage its body makes
them, as a [`_`](../tour/type-inference-and-annotations#partial-annotations) there is:

```nanyx fragment
def isLong: Time -> bool  -- [Num(a)] Time(a) -> bool
def overlap: Time, Time -> bool  -- [Num(a)] Time(a), Time(a) -> bool
def total: list(Time) -> Time  -- [Num(a)] list(Time(a)) -> Time(a), from its body
```

So `overlap(t, u)` is an error for a `t` stored as an `int` and a `u` stored as a `float`. Writing the storages says
otherwise: `Time(a), Time(b)` takes two, and in `Distance(a), Time` the bare `Time` has a storage of its own, apart from
`a`. A signature that names a dimension requires `Num` of its storage without writing it. A function with no body to say
what its result is, as an [`@extern`](../tour/attributes#extern) has, writes a storage its parameters fix, or a number
type, in its result ([NYX0293](../standard-tools/errors#nyx0293)). A constrained dimension is written without a storage,
`Bytes`, since it has only one ([NYX0294](../standard-tools/errors#nyx0294)).

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
| `a // n` | `Time(a)` | Floored to a whole number of default storage units |
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
println(2m * 3m)
println(1km / 1m)
-- 100.0m
-- 6m*m
-- 1000.0
```

Two storages on its parts, and a part that is no dimension, are errors
([NYX0295](../standard-tools/errors#nyx0295), [NYX0296](../standard-tools/errors#nyx0296)).

A function that divides or multiplies two of its parameters writes their types, as `speed` does. Without them, `/` is
read through [`Num`](./contexts#operators-find-their-context), whose `/` takes its operands as one type, so
`def speed = { d, t -> d / t }` called with a `Length` and a `Time` is a mismatch, which shows the def with the
parameters' types written. Called with two `Length`s, it is a missing `Num`, since a dimension has none (`Num`'s `*` would
make `m * m` a length), which shows the same.

## Units of a derived dimension

A derived dimension's units are defined from its parts' units, multiplied and divided, on one line and read from the
left: `unit kmh: Speed = 1km / 1hr`, or `unit g: Acceleration = 9.80665m / 1s / 1s`. It has no default storage or
display unit of its own, since its values are stored in its parts' default storage units and displayed in their default
display units, so a unit of one marked `@baseUnit` or `@storageUnit`, or not defined, is an error
([NYX0297](../standard-tools/errors#nyx0297)), as is a definition of another dimension
([NYX0289](../standard-tools/errors#nyx0289)). A unit can be defined from another of the dimension's units, its own or
imported: `unit mph: Speed = 1.609344kmh`.

A number with one is a value of the dimension, `90kmh`, stored as its number of the parts' default storage units, as
`500ms` is.
That is seldom a whole number, a kilometre an hour being 1/3_600_000_000 of a metre a nanosecond, so it is a `float`
unless the code around it expects an `int`, where it is an error that says so.

A value prints in its parts' default display units, joined by `*` and `/` with no spaces, and a divisor of more than
one part in brackets: `m/s`, `m*m`, `m/(s*s)`. Its own units make values but never print them: the units come from the
declarations, not from the value's size, so the output is predictable, as a ratio has no best unit (`1.0m/s`,
`1.0km/ks` and `0.001mm/ms` are one value). An `int` that is no whole number of the display units prints in the parts'
default storage units, the only ones it is exact in, and a value whose part's display unit the module can't see prints
as `dbg` writes it.

```nanyx
dimension Length

unit mm: Length
@baseUnit unit m: Length = 1_000mm
unit km: Length = 1_000m

dimension Speed = Length / Time

unit kmh: Speed = 1km / 1hr

def cruise = 90kmh
println(cruise)
println(36km / 0.5hr)
println(1m / 1s)
println(2000mm * 1000mm)
println(1500mm * 1mm)
-- 25.0m/s
-- 20.0m/s
-- 1.0m/s
-- 2m*m
-- 1500mm*mm
```

A `float` that arithmetic has left within a millionth of a millionth of a whole number counts as that whole number:
`90kmh + 10m / 1s` prints `35.0m/s`. A unit defined as nothing, `0m`, or divided by nothing, `1m / 0s`, is an
error ([NYX0298](../standard-tools/errors#nyx0298)).

# Printing

A measured value prints in a unit chosen by its storage:

- **An `int`** prints in the largest unit it is a whole number of, the only units it is exact in, and in the default
  storage unit when it is a whole number of no other: `1500ms`, `2min`.
- **A `float` or `F32`** prints in the largest unit its magnitude is at least 1 of, `1.5s`, `16.5ms`, `-1.5s`, and in
  the smallest unit when it is under 1 of every unit: `0.25ns`. It is written as a `float` is, so a whole number keeps
  its decimal point, as `println(2.0)` writes `2.0`: a `Time(float)` of `120s` prints `2.0min`.
- **Zero** prints in the default display unit, which is `s` for a `Time`: `0s`, `0.0s`.

```nanyx
def asFloat: Time(float) -> Time(float) = { t -> t }

println("{1500ms} {120s} {asFloat(1.5s)} {asFloat(16.5ms)}")
println("{asFloat(120s)} {0.25ns} {0s} {asFloat(0s)}")
println([1s, 2min, 3hr])
-- 1500ms 2min 1.5s 16.5ms
-- 2.0min 0.25ns 0s 0.0s
-- [1s, 2min, 3hr]
```

Only the units the printing module can write are tried: its own, and those it imports. A generic `Time(a)` is printed
in the default storage unit, `1500000000ns`, and needs `ToString(a)`, as choosing a unit needs a comparison; a function
that wants a unit chosen takes a `Time(int)` or a `Time(float)`. A value of a derived dimension is written in its
parts' default display units (see [Units of a derived dimension](#units-of-a-derived-dimension)), and an `int` that is
no whole number of them in their default storage units. `dbg` writes a measured value as its dimension around its number of default storage units,
`Time(1500000000)`.

[`format`](../stdlib/prelude-format) writes a measured value with a float's spec, in the unit named after a space at the
spec's end, or else in the unit it prints in, then the unit:

```nanyx
println(format(1500ms, ".1f ms"))
println(format(1500ms, "+.2f"))
println(format(2hr, ",.0f s"))
-- 1500.0ms
-- +1.50s
-- 7,200s
```

The spec may name any unit of the dimension the module can write, a derived dimension's own units among them, `kmh`,
or a composite of its base parts' units, joined by `*` and `/` and read from the left, whose powers make the dimension:
`{speed:.1f km/hr}`, `{area:.0f mm*mm}`. One whose units make another dimension, `km*hr` for a speed, says what they make.
A hole takes the same spec after a `:`, `"{elapsed:.1f ms}"`, and a unit the module can't write there is an error as the
program compiles ([NYX0307](../standard-tools/errors#nyx0307)).
Formatting needs the number as a float, so only a dimension stored as an `int`, a float or a sized integer narrower
than 64 bits has it.

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
| `i \shift(t)` | `Instant`: `i` moved by `t`, on if it is positive and back if it is negative, as `i \shift(-5s)` |
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
instants is the time between them, so an instant is moved back by name, `i \minus(t)` or `i \shift(-t)`: a type
attaches one `-`, and `i - 5s` is an error. `5s + i`, with the time on the left, is one too, as the operator is the one
its left operand's type has.
