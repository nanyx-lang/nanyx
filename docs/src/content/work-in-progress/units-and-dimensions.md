---
title: "Proposal: Units and dimensions"
description: "Compile-time units for numbers"
order: 2
---

> **Status: designed, being built.** The first step, single dimensions over any numeric storage, is settled below and is
> being built; derived dimensions such as `Length/Time` come second and still have open questions (see
> [Step 2](#step-2-derived-dimensions) and [Open questions](#open-questions)). The compiler reads unit literals and
> checks the declarations of dimensions and units, reads dimensions in types and types numbers with units, as
> [Dimensions in types](#dimensions-in-types) and [Literals](#literals) say ([Units of measure](../deep-dives/units) says
> how); it does not type arithmetic on measured values yet.

Nanyx will check units of measure at compile time, so a length cannot be added to a time, and a call reads as what
it means:

```nanyx fragment
sleep(500ms)
def lookback = 30d
def maxFileSize = 50MiB
```

# Dimensions and units

A **dimension** is a kind of quantity: `Time`, `Length`, `Bytes`. A dimension is a **type**, generic in the number
that stores it, as a `[Num(a)]` function is generic in its number type: `500ms` is a `Time(int)` and `0.5s` a
`Time(float)`, and a function that takes a `Time` takes either.

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

dimension Length(float)

@baseUnit unit m: Length
unit km: Length = 1_000m
unit mm: Length = 0.001m

dimension Bytes(int)

@baseUnit unit B: Bytes
unit KB: Bytes = 1_000B
unit KiB: Bytes = 1_024B
```

- `dimension Time` declares a dimension that any numeric type can store. `dimension Bytes(int)` constrains it to one
  storage type: a `Bytes` is always an `int`, and `Length` always a `float`. A `Money(Decimal)` would need a `Decimal`
  type, which the standard library does not have yet.
- `unit <name>: <dimension>` declares a unit. Each dimension has exactly one unit marked `@baseUnit`, its **base
  unit**: the unit its values are stored in (see [Storage](#storage)).
- Every other unit is declared with `=` as a constant multiple of a unit of the same dimension, so of the base unit in
  the end. The multiple need not be whole, and the base need not be the smallest unit: `mm` is `0.001m`. A chain of
  definitions that comes back to where it started is an error, as is a unit defined in another dimension's units.
- `dimension` and `unit` are not reserved words. They begin a declaration only at the start of a top-level line, with a
  name after them on it, so code that uses them as names keeps working. They are declared at the top level of a file or
  of a module block, not in a def's body.
- Units are names of their own kind, apart from values and types: `500ms` means the unit `ms` even where a value named
  `ms` is in scope. A module exports and imports dimensions and units as it does types, with `export` before the
  declaration: `export dimension Time` and `export unit ms: Time = 1_000us`. A unit can't be written qualified, so an
  import brings in the units its module exports unqualified, under an alias too, or only those it selects:
  `import time as (Time, ms)`. Two imports that bring in units of one name are an error only where the name is
  written, as two imports' values of one name are, and a unit the module declares itself is the one its name means.
- A module can add units to a dimension another module declares, `unit ps: Time = 0.001ns`, but not its base unit,
  which is declared with the dimension.

The base unit is declared, not worked out from the smallest unit in scope, so a unit declared in another module, such as
`unit ps: Time = 0.001ns`, can't change how a dimension is stored. A unit finer than the base is a fraction of it, which
a `float` stores and an `int` cannot (see [Literals](#literals)).

Months and years are not units, as their lengths vary; they belong to a calendar library over `Instant`. Byte units
are decimal or binary by name: `KB` is `1_000B` and `KiB` is `1_024B`.

# Storage

A `Time(a)` is an `a` at run time, measured in `Time`'s base unit: `500ms` is the `int` `500_000_000`. Dimensions and
units are erased after type checking, so a `Time(int)` costs what an `int` does, and a generic function is specialised
at each storage type it is called with, as one generic in a `[Num(a)]` is.

A dimension's storage always has a `Num` instance. A function that names a dimension requires `Num` of its storage
without writing it, so `list(Time) -> Time` below is checked as `[Num(a)] list(Time(a)) -> Time(a)`.

Choosing the base unit is choosing what the dimension's values can hold exactly. `Time`'s is `ns`, the smallest unit
anyone writes, so a `Time(int)` holds every whole number of nanoseconds for about 292 years either side of zero, the
range of `Instant`, and sums of whole units are exact: `100ms + 200ms == 300ms`. A `Length(float)` based on `m` holds
`5mm` as `0.005`, inexactly, as floats hold most decimals.

# Dimensions in types

A dimension written without its storage, as in `Time`, stands for a `Time` of any storage, and each one so written is
of a storage of its own. Writing the storage, as in `Time(a)`, is how a type says two dimensions share one:

```nanyx fragment
def isLong: Time -> bool
def overlap: Time, Time -> bool -- Time(a), Time(b) -> bool
def average: list(Time) -> Time -- error: the result's storage is not fixed by the parameter's
def average: list(Time(a)) -> Time(a) = { ts -> list.sum(ts) // fromInt(ts.length) }
def speed: Length(a), Time(a) -> Speed(a) = { d, t -> d / t } -- step 2: derived dimensions
```

A storage written only in the result, as an unwritten one always is, is an error that shows how to tie it to a
parameter's, as a context's parameter that is not the type of a parameter or the result is
([Associated types](../advanced/contexts#associated-types)). A constrained dimension is not generic: `Bytes` is
always `Bytes`, and `Bytes(a)` is an error.

# Literals

A number followed by a unit with no space between them is one literal: `500ms`, `1_000ns`, `0.5s`, `1.5e3ms`. Its type
is the unit's dimension, and the compiler scales it to the base unit: `0.5s` is stored as `500_000_000`. `100 ms` is
the number `100` followed by the name `ms`. Hexadecimal, octal and binary literals take no unit, as `0x1B` is a number,
and an exponent comes after a fraction, as in a float: `1.5e3ms`, not `1e3ms`.

A unit literal's storage is the one the code around it expects, as a plain literal's type is
([types](../tour/types)). Where nothing expects one, the storage is the constrained dimension's, or else the one the
number is written as: `500ms` is a `Time(int)` and `0.5s` a `Time(float)`. A program does not mix the two by accident:

```nanyx fragment
def t = 0.5s -- Time(float)
def u = 500ms -- Time(int)
def both = t + u -- error: Time(float) + Time(int)
def v: Time(float) = 500ms -- the expected storage
def w: Time(int) = 0.5s -- fine: 500_000_000ns is a whole number of ns
def x: Time(int) = 1.5ns -- error: not a whole number of ns
def y = 5mm -- Length(float), as Length is constrained to float
def z = 50.5KB -- Bytes: 50_500B
```

In integer storage a literal must be a whole number of base units, so where a whole number is written for a dimension
that is not constrained and it scales to a fraction of the base, that is an error with the fixes, writing it as a
decimal or constraining the dimension, not a quiet change to `float`. Beside a value of a generic `Time(a)`, a literal
that is a whole number of base units is `fromInt` of it, as a plain literal beside an `a` is in a `[Num(a)]` function
([Num(a)](../advanced/contexts#numa)): `0.5s` is `fromInt(500_000_000)`, and `1.5ns` is an error.

A literal with no unit is never a measured value: `sleep(0)` is an error. `zero`, from `Add`, is the zero of any
dimension.

# Arithmetic

`/` is fractional division and `//` integral division, floored, on every number type; `int / int` is a `float`
([Num(a)](../advanced/contexts#numa)). On a `Time(a)`, with `fraction` the type `a`'s `/` gives (a `float`
for the integer types):

| Expression | Type | |
|---|---|---|
| `a + b`, `a - b`, `-a` | `Time(a)` | Both `Time(a)`; through `Add` and `Sub` |
| `a == b`, `a < b`, ... | `bool` | Both `Time(a)` |
| `a * n`, `n * a` | `Time(a)` | Scaling by an `a` |
| `a / n` | `Time(fraction)` | A `Time(int)` divided by `2` is a `Time(float)` |
| `a // n` | `Time(a)` | Floored to a whole number of base units on integer storage |
| `a / b` | `fraction` | How many `b`s in `a`: `t / 1s` reads `t` in seconds, with the fraction |
| `a // b` | `a` | How many whole `b`s in `a`: `t // 1s` |
| `a % b` | `Time(a)` | What is left: `-1500ms // 1s` is `-2` and `-1500ms % 1s` is `500ms` |

A dimension has `Add` and `Sub` instances, so generic code such as `[Add(a)] a, a -> a` sums times. It has no `Num`
instance: `Num`'s `*` is `(a, a) -> a`, and a `Time` times a `Time` is not a `Time`. The compiler types `*`, `/`, `//`
and `%` on measured values itself, by the rules above, through the storage's `Num`.

A function that halves a time says which division it means:

```nanyx fragment
def half: Time(a) -> Time(fraction) = { t -> t / 2 }
def halfDown: Time(a) -> Time(a) = { t -> t // 2 }
```

# Display

A measured value prints in the largest unit in which it is a whole number: `"{1.5s}"` is `1500ms`, `"{120s}"` is
`2min`, and `"{16.5ms}"` is `16500us`. One that is not a whole number of any unit, such as `0.25ns`, prints in the base
unit. The dimension is known at compile time, so choosing the unit costs a division or two at run time. Printing a
generic `Time(a)` requires `ToString(a)`.

# Instant

`Instant` is a point in time: an `int` of nanoseconds since the start of 1970, UTC, which covers about 292 years either
side exactly. `Clock`'s `now` returns one.

| Expression | Type |
|---|---|
| `i - j` | `Time(int)` |
| `i + t`, `i - t` | `Instant`, for a `Time(int)` `t` |
| `i + j` | error |

# Step 2: derived dimensions

Not part of the first step, and not settled.

- **Derived dimensions.** `Length / Time` is a dimension, and `dimension Speed = Length / Time` names it, generic in
  its storage as its parts are: `Speed(a)`. The compiler stores a dimension as sorted base dimensions with integer
  exponents, so `Length / Time` and `Length * Time^-1` are the same dimension, and `*` and `/` add and subtract
  exponents: `100m / 9.58s` is a `Speed(float)`. Both sides share a storage, so a `Length(float)` divided by a
  `Time(int)` converts one first.
- **Integer storage.** `/` across two dimensions gives the `fraction` storage, so `50MB / 1s` is a `float`-stored
  `Bytes / Time`, not `0` bytes per nanosecond. `//` is only between values of one dimension or by a plain number, so
  a derived dimension is never truncated.
- **Dimension variables.** `def square: d -> d^2` (spelling open) needs unification that solves equations such as
  `d * d = Length^2`. This is the largest piece of the work; Kennedy's "Types for Units-of-Measure" gives an algorithm
  that fits a Hindley–Milner checker. `Time(a)` already means a `Time` stored as `a`, so a dimension variable needs a
  spelling of its own.
- **Affine quantities.** `Instant` and `Time` are a point and a distance. Temperature has the same shape (a
  temperature and a temperature difference). Open: whether the language has a general form for this, or each is a
  library type.

# Open questions

1. **Reading a generic time as an `int`.** A host's `sleep` wants whole milliseconds, `t // 1ms`, which is an `a` for a
   `Time(a)`; `Num` turns an `int` into an `a` (`fromInt`) but not back. Either `Num` gains a member that does, or the
   storage of a dimension needs a context of its own. Until then `sleep` takes a `Time(int)`, which every literal of a
   whole number of nanoseconds, `0.5s` included, is when it is passed to it.
2. **Constraining a dimension to a context.** `dimension Bytes(int)` names one type. An angle (`deg` is `pi / 180` of a
   `rad`) has no unit that the others are whole multiples of, so it wants any type with fractions, `F32` or `float`,
   which would need a constraint by context. Until then it is `dimension Angle(float)`. The spelling of an
   irrational scale, as in `unit deg: Angle = (pi / 180)rad`, is open too.
3. **Currencies.** A currency is not a fixed multiple of another, so `usd` and `eur` are not units of one `Money`
   dimension: each currency is a dimension, or the currency is a value.
4. **Units in patterns.** A number with a unit is not a pattern yet: `| 0ms ->` is a syntax error, and a guard,
   `| == 0ms ->`, compares with one. Open: whether a pattern matches a measured value exactly, which for a `float`
   storage is as fragile as matching a `float`, and whether a range of them, `0ms..1s`, is a pattern.

# Prior art

- **F#** puts the unit in the type: `float<ms>` and `float<s>` are different types, and converting is multiplying by a
  constant. Nanyx puts the dimension in the type, so `sleep` takes any `Time`.
- **Rust's uom** types quantities by dimension and stores them in a base unit, as Nanyx does, and is generic over the
  storage type, as Nanyx's dimensions are.
- **Kotlin's `Duration`**, Go's `time.Duration` and Rust's `std::time::Duration` are one dimension stored as whole
  nanoseconds, as a `Time(int)` is.
