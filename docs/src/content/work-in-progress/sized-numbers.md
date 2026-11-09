---
title: "Sized numbers"
description: "Numeric types of fixed widths, and what int and float mean"
order: 6
---

> **Status: proposal.** Today `int` is a 32-bit integer in the compiled program, and the sized types below do not
> exist yet. This page records the intended design. Tracked in [#342](https://github.com/nanyx-lang/nanyx/issues/342) (a 64-bit
> `int`) and [#343](https://github.com/nanyx-lang/nanyx/issues/343) (the sized types).

Nanyx has a few short built-in names for the types almost every program uses: `int`, `float`, `bool`, `string` and
`list`. Numbers also come in fixed widths, for binary data, compact records and talking to hosts, as ordinary named
types:

| Type | Values |
|------|--------|
| `I8`, `I16`, `I32`, `I64` | signed integers of 8, 16, 32 and 64 bits |
| `U8`, `U16`, `U32`, `U64` | unsigned integers of 8, 16, 32 and 64 bits |
| `F32`, `F64` | IEEE 754 floating-point numbers of 32 and 64 bits |

`int` is another name for `I64`, and `float` for `F64`. They are the same types, not conversions of them:

```nanyx
def total: int = 42
def same: I64 = total  -- no conversion needed
```

# The same width everywhere

`int` is 64 bits on every target the compiler supports, whatever the size of a pointer there. Nanyx has no
pointer-sized integer, like C#'s `nint` or Rust's `isize`:

- **A program computes the same results everywhere.** With a pointer-sized `int`, a sum that fits on a 64-bit
  target would overflow on a 32-bit one, such as WebAssembly today.
- **Nothing needs one.** Pointer-sized integers exist for pointer arithmetic and indexing raw memory, and Nanyx
  exposes neither.
- **32 bits are too few for a default.** They overflow at about 2.1 billion, which millisecond timestamps, file
  sizes and IDs all pass.

Sixty-four-bit arithmetic is native on every target, WebAssembly included, so the wider default costs little.

# Literals

A number literal takes the numeric type the code around it expects, and is `int` (or `float`, with a decimal point)
when nothing says otherwise:

```nanyx
def small: U8 = 200      -- a U8
def big = 3_000_000_000  -- an int
def ratio: F32 = 0.5     -- an F32
```

A literal that does not fit its type is a compile error: `def small: U8 = 300` is rejected.

# Arithmetic

Arithmetic and comparison work on two values of the same type, through the [`Num(a)`](../advanced/contexts)
context that every numeric type satisfies. Nothing is converted implicitly, so adding an `I32` to an `int` is a type
error until one side is [converted](#conversions).

[Units](./units-and-dimensions) attach to any of these types: `I64(Bytes)`, `F32(Time)`.

## Overflow

Integer arithmetic whose result does not fit its type stops the program with a runtime error, as division by zero
does. It never wraps around silently:

```nanyx
def big: I32 = 2_000_000_000
def bigger = big + big  -- runtime error: I32 overflow
```

This holds for every integer type, unsigned ones included: subtracting a larger `U32` from a smaller one is an
overflow like any other, not a wrap to 4 billion. A program that needs a negative difference converts to a signed type
first.

Code that wants arithmetic that wraps on purpose, such as a hash function, asks for it by name: `I32.wrappingAdd`,
`I32.wrappingSub` and `I32.wrappingMul`, on each integer type. That is the only way to get it, so a wrap is always
visible in the code.

Swift checks every operation in the same way; Rust checks in debug builds and wraps in release builds. Nanyx checks
in every build, so a program behaves the same however it was compiled. Checking costs a compare and a branch per
operation, which the [principles](../foundations/principles) accept in exchange for never computing a wrong answer
silently.

Floating-point arithmetic does not stop: it follows IEEE 754, giving infinities and NaN.

# Conversions

Conversions are [attached definitions](../advanced/attached-definitions) on the type being converted, named after
the type converted to: `I32.toInt`, `U8.toI32`, `I64.toFloat`, `F64.toF32`. The target is named by its short name
where it has one (`toInt` and `toFloat`, not `toI64` and `toF64`). Like any attached definition, a conversion can be
called by its qualified name or piped into:

```nanyx
def count: I32 = 7
def wide = I32.toInt(count)
def same = count \toInt
```

What a conversion takes and gives depends on what it can lose:

| Conversion | Example | Signature |
|------------|---------|-----------|
| Lossless: every value fits | `I32` to `int`, `U8` to `I32`, `F32` to `float` | `I32.toInt: I32 -> int` |
| Integer to a narrower integer, or between signed and unsigned | `int` to `I32`, `I32` to `U32` | `I64.toI32: I64 -> #some(I32) \| #outOfRange` |
| Integer to floating point | `int` to `float` | `I64.toFloat: I64 -> float` |
| Floating point to integer | `float` to `int` | `F64.toInt: F64, #truncate \| #round \| #floor \| #ceil -> I64` |
| Floating point to integer, checked | `float` to `int` | `F64.tryToInt: F64, #truncate \| #round \| #floor \| #ceil -> #some(I64) \| #outOfRange` |
| Floating point to a narrower floating point | `float` to `F32` | `F64.toF32: F64 -> F32` |

- **A narrowing integer conversion** is the one that can fail outright, so it returns a tag union and the caller
  decides what to do when the value doesn't fit.
- **Integer to floating point** rounds to the nearest representable value. A `float` holds integers exactly up to
  2<sup>53</sup>; past that it rounds, as in every language with IEEE 754 floats.
- **Floating point to integer** takes a tag saying how to drop the fraction: `x \toInt(#round)`. A value that is
  NaN, infinite or out of the integer type's range is an overflow, and stops the program. For input that might be
  any of those, `tryToInt` takes the same tag and returns `#outOfRange` instead of stopping.
- **Floating point to a narrower floating point** rounds to the nearest `F32`, and gives infinity past its range.

`int` is `I64`, so `int` values have the conversions attached to `I64`: `I64.toFloat` is the same definition
whichever name the value's type was written with.

Narrowing integer conversions have no wrapping form that keeps only the low bits, as `wrappingAdd` does for
arithmetic. One can be added later if code needs it.
