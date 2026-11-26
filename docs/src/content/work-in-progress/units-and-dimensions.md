---
title: "Proposal: Units and dimensions"
description: "Compile-time units for numbers"
order: 2
---

> **Status: step 1 built; step 2 open.** Dimensions of one kind, their units, numbers with units, the arithmetic on
> them, printing, `Time` and `Instant` are built, and [Units of measure](../advanced/units-of-measure) describes them
> ([Units of measure](../deep-dives/units) in Deep dives says how the compiler does it). This page keeps what is still
> to come: derived dimensions, below, and the [open questions](#open-questions).

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
- **Affine quantities.** `Instant` and `Time` are a point and a distance, and until this is settled an `Instant` is
  moved and measured with named functions (see [Instant](../advanced/units-of-measure#instant)). Temperature has the same shape (a
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
