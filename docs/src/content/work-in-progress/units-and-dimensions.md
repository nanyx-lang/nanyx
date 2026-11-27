---
title: "Proposal: Units and dimensions"
description: "Compile-time units for numbers"
order: 2
---

> **Status: step 1 and derived dimensions built; dimension variables and exponents open.** Dimensions, their units,
> numbers with units, the arithmetic on them, derived dimensions written with `*` and `/`, printing, `Time` and `Instant`
> are built, and [Units of measure](../advanced/units-of-measure) describes them ([Units of
> measure](../deep-dives/units) in Deep dives says how the compiler does it). This page keeps what is still open: the
> rest of [Step 2](#step-2-derived-dimensions), and the [open questions](#open-questions).

# Step 2: derived dimensions

Derived dimensions written with `*` and `/` are built: [Derived dimensions](../advanced/units-of-measure#derived-dimensions)
describes them, and [Units of measure](../deep-dives/units#derived-dimensions) in Deep dives says how. A storage is
written on each part, `Length(float) / Time(float)`, since a part is already a type the parser reads and a type printed
that way reads back as the same type. A derived dimension has units of its own, defined from its parts' units,
`unit kmh: Speed = 1km / 1hr`, which make values of it, and a value of one prints in its parts' default display units,
`1.0m/s` ([Units of a derived
dimension](../advanced/units-of-measure#units-of-a-derived-dimension)). What is still open:

- **Dimension variables.** `def square: d -> d^2` (spelling open) needs unification that solves equations such as
  `d * d = Length^2`. This is the largest piece of the work; Kennedy's "Types for Units-of-Measure" gives an algorithm
  that fits a Hindley–Milner checker. `Time(a)` already means a `Time` stored as `a`, so a dimension variable needs a
  spelling of its own ([#2817](https://github.com/nanyx-lang/nanyx/issues/2817),
  [#2627](https://github.com/nanyx-lang/nanyx/issues/2627)).
- **Affine quantities.** `Instant` and `Time` are a point and a distance, and a library type of its own answers it, with
  the operators it attaches: `i - j` is a `Time(int)` and `i + t` an `Instant` (see
  [Instant](../advanced/units-of-measure#instant)). Temperature has the same shape (a temperature and a temperature
  difference). Open: `i - t`, an instant moved back, which needs a second `-` for other operands, and `t + i`, with the
  measured value on the left, whose type has no `+` for an `Instant`; and whether the language has a general form for
  this, or each is a library type.

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
- **Rust's uom** types quantities by dimension and stores them in a default storage unit, as Nanyx does, and is generic over the
  storage type, as Nanyx's dimensions are.
- **Kotlin's `Duration`**, Go's `time.Duration` and Rust's `std::time::Duration` are one dimension stored as whole
  nanoseconds, as a `Time(int)` is.
