---
title: "Proposal: Units and dimensions"
description: "Compile-time units for numbers"
order: 2
---

> **Status: designed, not built.** The first step, single dimensions, is settled below and is being built; derived
> dimensions such as `Length/Time` come second and still have open questions (see [Step 2](#step-2-derived-dimensions)).
> The code on this page is proposed syntax, which the compiler does not read yet.

Nanyx will check units of measure at compile time, so a length cannot be added to a time, and a call reads as what
it means:

```nanyx fragment
sleep(500ms)
def lookback = 30d
def maxFileSize = 50MiB
```

# Dimensions and units

A **dimension** is a kind of quantity: `Time`, `Length`, `Bytes`. A dimension is a **type**, and its values are
quantities of it: `500ms` and `2min` are both `Time`, and `sleep: [Async] Time -> ()` takes either.

A **unit** is a named scale within one dimension: `ns`, `ms`, `s`. Units make values (`500ms`) and read them back
(`t / 1s`); they are never part of a type. A function takes a `Time`, not a time in milliseconds.

`Time` is the dimension of physics and mechanics: how long something takes. A point in time is a different type,
`Instant`, like .NET's `DateTime` (see [Instant](#instant)).

# Declaring dimensions and units

```nanyx fragment
dimension Time

@baseUnit unit ns: Time
unit us: Time = 1_000ns
unit ms: Time = 1_000us
unit s: Time = 1_000ms
unit min: Time = 60s
unit hr: Time = 60min
unit d: Time = 24hr
```

- `dimension` declares a dimension, which is a type.
- `unit <name>: <dimension>` declares a unit. Each dimension has exactly one unit marked `@baseUnit`, and it is the
  dimension's **smallest** unit.
- Every other unit is declared with `=` as a whole multiple of a unit of the same dimension, so of the base unit in
  the end. A chain of definitions that comes back to where it started is an error, as is a unit defined in another
  dimension's units.
- `dimension` and `unit` are not reserved words. They begin a declaration only at the start of a top-level line, so
  code that uses them as names keeps working.
- Units are names of their own kind, apart from values and types: `500ms` means the unit `ms` even where a value named
  `ms` is in scope. A module exports and imports units as it does types.

Months and years are not units, as their lengths vary; they belong to a calendar library over `Instant`. Byte units
are decimal or binary by name: `KB` is `1_000B` and `KiB` is `1_024B`.

# Storage

A dimension's values are `f64`s at run time, measured in its base unit. Dimensions and units are erased after type
checking: a `Time` costs what a `float` does.

Because the base is the smallest unit and every unit is a whole multiple of it, sums of whole units are exact:
`100ms + 200ms == 300ms`, which would not hold with `s` as the base (`0.1 + 0.2` is not `0.3`). With `ns` as `Time`'s
base, an `f64` holds every whole number of nanoseconds up to about 104 days exactly.

# Literals

A number followed by a unit with no space between them is one literal: `500ms`, `1_000ns`, `0.5s`, `1.5e3ms`. Its type
is the unit's dimension, and the compiler scales it to the base unit: `0.5s` is stored as `500_000_000.0`. `100 ms` is
the number `100` followed by the name `ms`. Hexadecimal, octal and binary literals take no unit, as `0x1B` is a number.

A literal with no unit is never a measured value: `sleep(0)` is an error. `zero`, from `Add`, is the zero of any
dimension.

# Arithmetic

| Expression | Type | |
|---|---|---|
| `a + b`, `a - b`, `-a` | `Time` | Both `Time`; through `Add` and `Sub` |
| `a == b`, `a < b`, ... | `bool` | Both `Time` |
| `a * 2.0`, `2.0 * a`, `a / 2.0` | `Time` | Scaling by a plain number |
| `a / b` | `float` | Both `Time`: how many `b`s in `a`, so `t / 1s` reads `t` in seconds |
| `a % b` | `Time` | Both `Time` |

A dimension has `Add` and `Sub` instances, so generic code such as `[Add(a)] a, a -> a` sums times. It has no `Num`
instance: `Num`'s `*` is `(a, a) -> a`, and a `Time` times a `Time` is not a `Time`. The compiler types `*` and `/` on
measured values itself, by the rules above.

# Display

A measured value prints in the largest unit in which it is a whole number: `"{1.5s}"` is `1500ms`, `"{120s}"` is
`2min`, and `"{16.5ms}"` is `16500us`. The dimension is known at compile time, so this costs nothing at run time.

# Instant

`Instant` is a point in time: an `int` of nanoseconds since the start of 1970, UTC, which covers about 292 years either
side exactly. `Clock`'s `now` returns one.

| Expression | Type |
|---|---|
| `i - j` | `Time` |
| `i + t`, `i - t` | `Instant` |
| `i + j` | error |

# Step 2: derived dimensions

Not part of the first step, and not settled.

- **Derived dimensions.** `Length / Time` is a type expression, and `type Speed = Length / Time` names it. The compiler
  stores a dimension as sorted base dimensions with integer exponents, so `Length / Time` and `Length * Time^-1` are
  the same type, and `*` and `/` add and subtract exponents: `100m / 9.58s` is a `Length / Time`.
- **Dimension variables.** `def square: d -> d^2` (spelling open) needs unification that solves equations such as
  `d * d = Length^2`. This is the largest piece of the work; Kennedy's "Types for Units-of-Measure" gives an algorithm
  that fits a Hindley–Milner checker.
- **Integer dimensions.** `dimension Bytes = int` would store a dimension as an `int`. On one, a unit must not be a
  fraction of another, `b / c` is a `float`, and scaling by a `float` is an error. Open: what a derived dimension of an
  `int` and a `float` dimension is stored as.
- **Affine quantities.** `Instant` and `Time` are a point and a distance. Temperature has the same shape (a
  temperature and a temperature difference). Open: whether the language has a general form for this, or each is a
  library type.

# Prior art

- **F#** puts the unit in the type: `float<ms>` and `float<s>` are different types, and converting is multiplying by a
  constant. Nanyx puts the dimension in the type, so `sleep` takes any `Time`.
- **Rust's uom** types quantities by dimension and stores them in a base unit, as Nanyx does, and is generic over the
  storage type.
- **Kotlin's `Duration`** is one dimension stored as whole nanoseconds, made with `500.milliseconds`.
