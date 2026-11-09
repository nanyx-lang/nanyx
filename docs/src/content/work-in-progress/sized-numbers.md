---
title: "Sized numbers"
description: "Numeric types of fixed widths, and what int and float mean"
order: 6
---

> **Status: proposal.** Today `int` is a 32-bit integer in the compiled program, and the sized types below do not
> exist yet. This page records the intended design and the questions that remain open; see
> [Open questions](#open-questions) at the end. Tracked in [#342](https://github.com/nanyx-lang/nanyx/issues/342) (a 64-bit
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

# Arithmetic and conversions

Arithmetic and comparison work on two values of the same type, through the [`Num(a)`](../advanced/contexts)
context that every numeric type satisfies. Nothing is converted implicitly, so adding an `I32` to an `int` is a type
error until one side is converted. Conversions are explicit:

- A conversion that cannot lose information, such as `I32` to `I64`, always succeeds.
- A conversion that can returns a tag union, with the value when it fits: `#some(I32) | #outOfRange`.

[Units](./units-and-dimensions) attach to any of these types: `I64(Bytes)`, `F32(Time)`.

# Open questions

1. **Overflow.** Does integer arithmetic that leaves its type's range wrap around (as WebAssembly's does), or stop
   the program? Wrapping is free but silent; stopping is safer and costs a check per operation.
2. **Conversion names.** What are the conversion functions called, and should there also be a wrapping conversion
   for code that wants the low bits on purpose?
3. **Unsigned arithmetic.** Is subtraction that goes below zero on a `U32` an overflow like any other, or are
   unsigned types limited to storage and bit operations?
