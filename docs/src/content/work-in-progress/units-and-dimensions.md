---
title: "Units and dimensions"
description: "Compile-time units for numbers"
order: 2
---

> **Status: proposal.** Units of measure are not yet implemented. This page records the intended design and the questions that remain open; see [Open questions](#open-questions) at the end. The code on this page is proposed syntax, which the compiler does not read yet.

Nanyx will support units of measure for numeric types, allowing you to express time, sizes, and rates safely.

```nanyx fragment
def lookback = 30d
def timeout = 3000ms
def maxFilesize = 50MB
```

This page defines the design direction for units, with two goals:

1. Unit-safe arithmetic with automatic unit derivation (for example `m / s -> m/s`)
2. Ergonomic value construction at call sites (for example `delay(500ms)`)

# Two concepts: dimensions and units

Nanyx should model **dimensions** and **units** separately:

- **Dimension**: the physical kind used by type checking (`Time`, `Length`, `Mass`, `Length/Time`)
- **Unit**: a named scale for a dimension (`ms`, `s`, `min`, `km`)

Type checking operates on dimensions. Units are syntax and conversion metadata for constructing and displaying values.

# Defining units

Units are always numeric in Nanyx. Unit declarations do not specify an underlying primitive type.

```nanyx fragment
dimension Time
dimension Bytes
dimension Length

@baseUnit unit(m) Metres: Length

@baseUnit unit(s) Seconds: Time
unit(ms) Milliseconds: Time = 0.001s
unit(min) Minutes: Time = 60s
unit(hr) Hours: Time = 60min
unit(d) Days: Time = 24hr

@baseUnit unit(B) Byte: Bytes
unit(KB) Kilobyte: Bytes = 1024B
unit(MB) Megabyte: Bytes = 1024KB
```

## Base vs derived units

A dimension defines at least one base unit.

- A unit marked with `@baseUnit` is a base unit.
- A unit declared with `=` is a derived unit.
- `=` never defines a base unit; it always means “defined in terms of other units”.

Every derived unit must resolve through a finite chain to a base unit of the same dimension.

```nanyx fragment
dimension Time

@baseUnit unit(s) Seconds: Time

unit(ms) Milliseconds: Time = 0.001s
unit(min) Minutes: Time = 60s
```

Whether a dimension may have more than one base unit (for example both `s` and `tick` for `Time`) is unresolved; see [Open questions](#open-questions).

Cycles are invalid and must be rejected by the compiler:

```nanyx fragment
unit(a) A: Time = 2b
unit(b) B: Time = 3a
-- error: cyclic derived-unit definition (A -> B -> A)
```

## Design model

Each measured numeric type carries a compile-time _dimension vector_.

This page writes a measured type as `number(Dimension)`. The dimension is expected to become a parameter on the concrete numeric type instead, for example `float(Time)` or `int(Bytes)` (see [Arithmetic on measured values](#arithmetic-on-measured-values)); the rules below are unaffected by that spelling.

- `number` is dimensionless (`1`)
- Base dimensions are independent (for example `Length`, `Time`, `Mass`)
- Units map to a scale within one dimension

Conceptually:

- `number(Length)` means a quantity of length
- `number(Time)` means a quantity of time
- `number(Length/Time)` is speed
- `number(Mass*Length/Time^2)` is force

The compiler stores normalized dimensions (sorted bases + integer exponents), so equivalent forms unify:

- `Length/Time` == `Length*Time^-1`
- `(Length/Time)*Time` == `Length`

# Derived units

```nanyx fragment
def transferRate = 50MB / s
```

# Type rules (F#-style behavior)

Dimensions participate in type inference and unification, not runtime values.

For arithmetic operators on measured numeric values:

- `+` and `-` require identical dimensions
- `*` adds exponents (`d1 * d2`)
- `/` subtracts exponents (`d1 / d2`)
- `pow(x, n)` multiplies exponents by `n` (for integer `n`)

Examples:

```nanyx fragment
def d = 100m
def t = 9.58s
def v = d / t -- inferred: number(Length/Time)
def a = v / t -- inferred: number(Length/Time^2)
```

Addition mismatch is rejected:

```nanyx fragment
def bad = 10m + 2s
-- error: cannot add number(Length) and number(Time)
```

## Generic constraints over dimensions

Functions can stay generic over dimensions:

```nanyx fragment
def square: number(d) -> number(d^2) = { x -> x * x }
def avgSpeed: (number(Length), number(Time)) -> number(Length/Time) = { dist, time -> dist / time }
```

This keeps APIs reusable while preserving dimension correctness.

## Arithmetic on measured values

The prelude's numeric contexts are `Add(a)`, with `zero`, `+`, `-` and `negate`, and `Num(a)`, which adds `*`, `/`,
`%` and `fromInt` ([Add(a)](../advanced/contexts#adda), [Num(a)](../advanced/contexts#numa)). Units need `*` and `/` to
change the type: `float(Length) / float(Time)` is `float(Length/Time)`. No context of the form `(a, a) -> a` can say
that, so the plan is:

- **Measured types have `Add` but not `Num`.** `+`, `-` and `negate` on measured values go through `Add`, which
  requires identical dimensions: `3m + 4m` is a length, but `3m * 4m` is an area.
- **`*` and `/` on measured values** are typed by the dimension rules above, by the compiler, not through a context:
  when either operand is measured, `a * b` is the underlying type's multiplication at the product of the dimensions.
  Scaling, `2.0 * 3m`, is the case where one dimension is `1`.
- **Generic code over dimensions** uses dimension variables, as `float(d) -> float(d^2)` does, not `Num`. A
  `[Num(a)] square` cannot be called with a length; a dimension-generic `square` can.
- **`%` on measured values** keeps the dimension (`7m % 2m` is `1m`); `**` with a literal exponent multiplies it, as
  `pow` does.

So the dimension is a parameter of the concrete numeric type, and `Num` is the context of dimensionless numbers.

**Alternative.** *Contexts with an output type* (Rust's `Mul<Rhs, Output = ...>`), so `*` stays a context member and
units are ordinary instances. That needs multi-parameter contexts, associated types and instances that compute
dimensions, all to describe something the compiler has to know about anyway to unify `Length/Time` with
`Length*Time^-1`.

# Conversion rules

Unit declarations define scale factors between units of the same dimension.

```nanyx fragment
@baseUnit unit(s) Seconds: Time
unit(ms) Milliseconds: Time = 0.001s
unit(min) Minutes: Time = 60s
```

Conversions are compile-time checked and only allowed within equivalent dimensions:

```nanyx fragment
def timeout: number(Time) = 500ms
def twoMinutes = 120s as min -- explicit conversion syntax (proposed)
```

Invalid conversion is rejected:

```nanyx fragment
def impossible = 1m as s
```

# Ergonomics for `delay(500ms)`

Use unit suffix literals as typed numeric constructors.

- `500ms` parses as numeric literal `500` with unit suffix `ms`
- Type is inferred as `number(Time)` (carrying source unit metadata for conversions/formatting)
- Works anywhere a numeric expression works

```nanyx fragment
type Duration = number(Time)

def delay: Duration -> () = { d -> ... }

delay(500ms)      -- ok
delay(0.5s)       -- ok
delay(2min)       -- ok
delay(42)         -- error: expected Duration, found dimensionless number
```

# Function parameters: dimensions vs units

Both are useful, but they should mean different things.

- **Default**: functions accept a dimension (`number(Time)`, `number(Length/Time)`, etc.)
- **Optional strict mode**: functions can request an exact unit when protocol/interop requires it (for example milliseconds only)

Dimension-first API (recommended):

```nanyx fragment
def delay: number(Time) -> () = { d -> ... }

delay(500ms)
delay(0.5s)
delay(2min)
```

Unit-specific API (proposed constraint form):

```nanyx fragment
def setTimeoutRaw: number(Time) where unit = ms -> () = { d -> ... }

setTimeoutRaw(500ms)   -- ok
setTimeoutRaw(1s)      -- error (or require explicit `as ms`)
```

Recommendation: keep most APIs dimension-based, and reserve unit-specific parameters for boundaries (wire formats, hardware APIs, legacy contracts).

## Why this satisfies both goals

- Goal 1: unit algebra happens in inference/unification, so results derive automatically
- Goal 2: suffix literals construct measured values directly at call sites

# Runtime model

Dimensions and units are erased after type checking.

- Runtime carries only the underlying numeric representation
- No runtime penalty for unit checking
- Optional debug metadata can be emitted by tooling, not required by execution

# Open questions

The following need answers before this design can be implemented. They are listed in the order in which their answers constrain the rest.

1. **Where the scale lives.** If the unit is not part of the type, then `500ms + 1s` must be computed in a canonical unit, which means every literal is converted to a per-dimension base at compile time and the "source unit metadata" mentioned above is for display only. That in turn requires exactly one base unit per dimension, or a declared scale between any additional base units. The current text allows several base units with no relation between them, which cannot work.
2. **Integers.** `0.001s` and `1024B` mean conversions are not closed over `int`. If the canonical unit for `Time` is `s`, then `500ms` is `0.5` and every `Time` is a float. For integer-flavoured dimensions such as `Bytes` the base unit probably needs to be the smallest unit, with larger units as integer multiples.
3. **Interaction with `Num(a)`.** Answered by [Arithmetic on measured values](#arithmetic-on-measured-values): a dimension parameter on the concrete numeric type (`float(Time)`, `int(Bytes)`), which also gives a per-dimension answer to question 2. Measured types have `Add` but not `Num`, and `*` and `/` on them follow the dimension rules above. The sized numbers, `I8` to `U64` and `F32`, have their `Add` and `Num` instances already ([#343](https://github.com/nanyx-lang/nanyx/issues/343)).
4. **Type-level exponent arithmetic.** `number(d^2)` and `Length/Time^2` need integer exponents in the type language and unification that can solve equations such as `d * d = Length^2`. This is the largest implementation piece; Kennedy's "Types for Units-of-Measure" describes an algorithm that fits an HM-style checker.
5. **Lexing of suffix literals.** `100m` must be a single literal while `100 m` is not, `m` must not collide with an identifier in scope (such as an import alias), and the interaction with exponent notation (`1e5`) and underscores (`1_000ms`) needs stating.
