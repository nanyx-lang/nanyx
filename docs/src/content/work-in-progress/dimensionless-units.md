---
title: "Proposal: Dimensionless units"
description: "Percentages, basis points and parts per million as measured values, so that 10pc can't be mistaken for 0.1 or 10"
order: 2
---

> **Status: proposal.** Nothing here is built. It extends [Units and dimensions](./units-and-dimensions), whose first
> step is being built, and leans on its [Step 2](./units-and-dimensions#step-2-derived-dimensions) for how a ratio
> multiplies another dimension.

A percentage passed around a program as a plain number is ambiguous. `0.1` might be ten percent written as a fraction,
or a tenth of a percent written in percent, and a function that takes a `float` can't say which it means. Each caller
has to know, and a mistake is out by a factor of a hundred, which no test of the types catches.

This proposal makes a percentage a measured value, as a time is. `10pc` is ten percent wherever it goes, a function
that takes a `Ratio` can't be passed a bare `0.1`, and the unit it was written in doesn't matter to the code that reads
it:

```nanyx fragment
def discount: Ratio = 10pc
def fee = 25bp -- a quarter of a percent
def total = price - price * discount -- a Money, if price is one
```

# Today

A program can already give percentages a dimension of their own:

```nanyx fragment
dimension Share

@baseUnit unit pc: Share

def off: Share(int) = 10pc

def apply: int, Share(int) -> int = { amount, p ->
  amount * (p // 1pc) // 100
}
```

That keeps `10pc` apart from `10` and from `0.1`, but not much else:

- **It can't scale anything.** A `Share` times a `Time` is an error, as a `Time` times a `Time` is, so every use reads
  the number of percent out (`p // 1pc`) and divides by a hundred by hand, which is the conversion the dimension was
  meant to keep in one place.
- **Its base unit is a percent,** so `0.5pc` is not a whole number of it and needs `float` storage. Making `bp` the base
  instead makes `apply` divide by ten thousand.
- **Every library declares its own.** One library's `Share` is not another's, so two of them can't pass a percentage
  between them.

# A dimension with no dimensions

A **dimensionless** dimension is one whose values are plain numbers in meaning, which a type keeps apart from plain
numbers. The prelude declares one, `Ratio`, with the units people write ratios in:

```nanyx fragment
@dimensionless dimension Ratio

@baseUnit unit ppm: Ratio = 0.000_001
unit bp: Ratio = 100ppm
unit pm: Ratio = 1_000ppm
unit pc: Ratio = 10_000ppm
```

- `@dimensionless` marks a dimension whose base unit is a number. Its base unit is declared with `=`, as other units
  are, but as a plain number: `ppm` is a millionth. Every other dimension's base unit has no `=`, and only a
  dimensionless one's can have one.
- `ppm` is the base unit so that every unit is a whole number of it: `10pc` is stored as the `int` `100_000`, `0.5pc` as
  `5_000` and `25bp` as `2_500`, so sums of percentages are exact in `int` storage. `0.5ppm` is not a whole number of
  base units, and is an error in `int` storage, as `1.5ns` is in a `Time(int)`.
- A `Ratio` is generic in its storage, as a `Time` is: `10pc` is a `Ratio(int)` and `0.1pc` is too, while `0.25ppm` is a
  `Ratio(float)`.
- A program can declare dimensionless dimensions of its own. They are not `Ratio`s, so a `Probability` is not a
  `Ratio` unless a function converts it.

# Arithmetic

Between two `Ratio`s, and between a `Ratio` and a plain number, the rules are those of every dimension
([Arithmetic](./units-and-dimensions#arithmetic)): `10pc + 5pc` is `15pc`, `10pc * 2` is `20pc`, and `10pc + 0.1` is an
error. What is new is a `Ratio` times another measured value:

| Expression | Type | |
|---|---|---|
| `t * r`, `r * t` | `Time(fraction)` | A time `t` scaled by the ratio `r`: `2s * 10pc` is `200ms` |
| `r * s` | `Ratio(fraction)` | A ratio of a ratio: `10pc * 10pc` is `1pc` |
| `n * r` | `Ratio(a)` | Scaling the ratio by a plain number `n`, as for any dimension |
| `r / 100pc` | `fraction` | The ratio as a plain fraction: `10pc / 100pc` is `0.1` |
| `r / 1pc` | `fraction` | The ratio in percent: `10pc / 1pc` is `10` |
| `f * 100pc` | `Ratio(float)` | A plain fraction as a ratio: `0.1 * 100pc` is `10pc` |

The result of scaling by a ratio is of the `fraction` storage, as `a / n` is, because it divides by the base unit's
million: `3ns * 50pc` is `1.5ns`, which a `Time(int)` can't hold. A program that wants it whole floors it,
`(t * r) // 1ns * 1ns`, or rounds it (see [Open questions](#open-questions)).

In [Step 2](./units-and-dimensions#step-2-derived-dimensions)'s terms, a dimensionless dimension has no base dimensions,
so multiplying by it leaves the other dimension as it was. It is still a type of its own, not a plain number, which is
the point of it. A dimension divided by itself, `t / u`, stays a plain `fraction`, as it is today: it has no unit to
choose between `Ratio` and any other dimensionless dimension.

A plain number times a `Ratio` is a `Ratio`, as a plain number times a `Time` is a `Time`, so a ratio of a plain
amount is read through the fraction:

```nanyx fragment
def saving: int, Ratio -> float = { amount, r -> amount * (r / 100pc) }
```

When the amount is a measured value, `Money` say, no conversion is written: `price * 10pc` is a `Money`.

# Display

A `Ratio` prints as a percentage, whatever unit it was written in: `"{25bp}"` is `0.25pc`, and `"{10pc}"` is `10pc`. The
[display rule for other dimensions](./units-and-dimensions#display), the largest unit in which the value is whole, would
print `12.5pc` as `125pm`, which nobody reads easily. A dimensionless dimension names the unit it prints in, which for
`Ratio` is `pc`; its spelling is an [open question](#open-questions).

# Open questions

1. **Spelling the units.** `pc` is short for percent, but in science it is the parsec, and `pct` is the more common
   abbreviation. `pm` for per mille reads as picometres, and `permille` or no unit at all may be better. `%` and `‰`
   can't follow a number, as `%` is the remainder operator and units are names.
2. **Marking a dimension dimensionless.** `@dimensionless` is an attribute, as `@baseUnit` is. The base unit's `=` with
   a plain number might be enough on its own to say it, `@baseUnit unit ppm: Ratio = 0.000_001`, without the attribute.
3. **Plain number times a ratio.** `2603 * 10pc` is a `Ratio` above, for consistency with other dimensions, but what
   people usually mean is ten percent of `2603`. Making a plain number times a ratio a plain number would make
   `10pc * 2` a `Ratio` and `2 * 10pc` a number, which is worse. A library function, `ratio.of(r, amount)`, may be
   clearer than `amount * (r / 100pc)`.
4. **Rounding to integer storage.** Money in pence is an `int`, and ten percent of it is not always whole. A rounding
   mode belongs with `Decimal`, which the standard library doesn't have yet; until then the program floors or rounds
   the `fraction` itself.
5. **Naming the display unit.** A dimensionless dimension needs a way to name the unit it prints in,
   `@displayUnit unit pc: Ratio = 10_000ppm` perhaps, and other dimensions might want it too (`Bytes` printed in `MiB`).
6. **Angles.** In SI a radian is dimensionless, a length divided by a length. Whether `Angle` should be a dimensionless
   dimension, so that `r * 1rad` is a length for a radius `r`, or stay a dimension of its own, is open.
7. **Which ratios are `Ratio`s.** A probability, a gain in decibels and a growth rate are all dimensionless, but adding
   one to another is usually a mistake. The prelude's `Ratio` is for proportions of a whole; a program declares the
   others as dimensions of their own.

# Prior art

- **F#** has a dimensionless measure, `float<1>`, which is a plain `float`. A percent is a measure of its own,
  `[<Measure>] type percent`, with its conversion to a fraction written by hand.
- **Rust's uom** has a `Ratio` quantity with `percent`, `per_mille`, `basis_point` and `part_per_million` units, stored
  in its base unit, the plain fraction.
- **Frink** and **GNU units** treat `percent` as the number `0.01`, so a percentage is a plain number with a scale, which
  doesn't keep `10pc` apart from `0.1`.
