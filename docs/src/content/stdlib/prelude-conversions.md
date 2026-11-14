---
title: "prelude: conversions"
description: Converting between int, float and the sized number types
order: 6
---

Conversions between `int`, `float` and the [sized number types](../advanced/sized-numbers), and reading the sized number types from text. They are in the [prelude](prelude), so no import is needed. A conversion that cannot hold every value gives back `#outOfRange` for one it cannot.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `int.toI8`

```nanyx fragment
def int.toI8: int -> #some(I8) | #outOfRange
```

## `int.toI16`

```nanyx fragment
def int.toI16: int -> #some(I16) | #outOfRange
```

## `int.toI32`

```nanyx fragment
def int.toI32: int -> #some(I32) | #outOfRange
```

## `int.toU8`

```nanyx fragment
def int.toU8: int -> #some(U8) | #outOfRange
```

## `int.toU16`

```nanyx fragment
def int.toU16: int -> #some(U16) | #outOfRange
```

## `int.toU32`

```nanyx fragment
def int.toU32: int -> #some(U32) | #outOfRange
```

## `int.toU64`

```nanyx fragment
def int.toU64: int -> #some(U64) | #outOfRange
```

## `int.toF32`

```nanyx fragment
def int.toF32: int -> F32
```

## `I8.toInt`

```nanyx fragment
def I8.toInt: I8 -> int
```

## `I8.toI16`

```nanyx fragment
def I8.toI16: I8 -> I16
```

## `I8.toI32`

```nanyx fragment
def I8.toI32: I8 -> I32
```

## `I8.toU8`

```nanyx fragment
def I8.toU8: I8 -> #some(U8) | #outOfRange
```

## `I8.toU16`

```nanyx fragment
def I8.toU16: I8 -> #some(U16) | #outOfRange
```

## `I8.toU32`

```nanyx fragment
def I8.toU32: I8 -> #some(U32) | #outOfRange
```

## `I8.toU64`

```nanyx fragment
def I8.toU64: I8 -> #some(U64) | #outOfRange
```

## `I8.toFloat`

```nanyx fragment
def I8.toFloat: I8 -> float
```

## `I8.toF32`

```nanyx fragment
def I8.toF32: I8 -> F32
```

## `I16.toInt`

```nanyx fragment
def I16.toInt: I16 -> int
```

## `I16.toI8`

```nanyx fragment
def I16.toI8: I16 -> #some(I8) | #outOfRange
```

## `I16.toI32`

```nanyx fragment
def I16.toI32: I16 -> I32
```

## `I16.toU8`

```nanyx fragment
def I16.toU8: I16 -> #some(U8) | #outOfRange
```

## `I16.toU16`

```nanyx fragment
def I16.toU16: I16 -> #some(U16) | #outOfRange
```

## `I16.toU32`

```nanyx fragment
def I16.toU32: I16 -> #some(U32) | #outOfRange
```

## `I16.toU64`

```nanyx fragment
def I16.toU64: I16 -> #some(U64) | #outOfRange
```

## `I16.toFloat`

```nanyx fragment
def I16.toFloat: I16 -> float
```

## `I16.toF32`

```nanyx fragment
def I16.toF32: I16 -> F32
```

## `I32.toInt`

```nanyx fragment
def I32.toInt: I32 -> int
```

## `I32.toI8`

```nanyx fragment
def I32.toI8: I32 -> #some(I8) | #outOfRange
```

## `I32.toI16`

```nanyx fragment
def I32.toI16: I32 -> #some(I16) | #outOfRange
```

## `I32.toU8`

```nanyx fragment
def I32.toU8: I32 -> #some(U8) | #outOfRange
```

## `I32.toU16`

```nanyx fragment
def I32.toU16: I32 -> #some(U16) | #outOfRange
```

## `I32.toU32`

```nanyx fragment
def I32.toU32: I32 -> #some(U32) | #outOfRange
```

## `I32.toU64`

```nanyx fragment
def I32.toU64: I32 -> #some(U64) | #outOfRange
```

## `I32.toFloat`

```nanyx fragment
def I32.toFloat: I32 -> float
```

## `I32.toF32`

```nanyx fragment
def I32.toF32: I32 -> F32
```

## `U8.toInt`

```nanyx fragment
def U8.toInt: U8 -> int
```

## `U8.toI8`

```nanyx fragment
def U8.toI8: U8 -> #some(I8) | #outOfRange
```

## `U8.toI16`

```nanyx fragment
def U8.toI16: U8 -> I16
```

## `U8.toI32`

```nanyx fragment
def U8.toI32: U8 -> I32
```

## `U8.toU16`

```nanyx fragment
def U8.toU16: U8 -> U16
```

## `U8.toU32`

```nanyx fragment
def U8.toU32: U8 -> U32
```

## `U8.toU64`

```nanyx fragment
def U8.toU64: U8 -> U64
```

## `U8.toFloat`

```nanyx fragment
def U8.toFloat: U8 -> float
```

## `U8.toF32`

```nanyx fragment
def U8.toF32: U8 -> F32
```

## `U16.toInt`

```nanyx fragment
def U16.toInt: U16 -> int
```

## `U16.toI8`

```nanyx fragment
def U16.toI8: U16 -> #some(I8) | #outOfRange
```

## `U16.toI16`

```nanyx fragment
def U16.toI16: U16 -> #some(I16) | #outOfRange
```

## `U16.toI32`

```nanyx fragment
def U16.toI32: U16 -> I32
```

## `U16.toU8`

```nanyx fragment
def U16.toU8: U16 -> #some(U8) | #outOfRange
```

## `U16.toU32`

```nanyx fragment
def U16.toU32: U16 -> U32
```

## `U16.toU64`

```nanyx fragment
def U16.toU64: U16 -> U64
```

## `U16.toFloat`

```nanyx fragment
def U16.toFloat: U16 -> float
```

## `U16.toF32`

```nanyx fragment
def U16.toF32: U16 -> F32
```

## `U32.toInt`

```nanyx fragment
def U32.toInt: U32 -> int
```

## `U32.toI8`

```nanyx fragment
def U32.toI8: U32 -> #some(I8) | #outOfRange
```

## `U32.toI16`

```nanyx fragment
def U32.toI16: U32 -> #some(I16) | #outOfRange
```

## `U32.toI32`

```nanyx fragment
def U32.toI32: U32 -> #some(I32) | #outOfRange
```

## `U32.toU8`

```nanyx fragment
def U32.toU8: U32 -> #some(U8) | #outOfRange
```

## `U32.toU16`

```nanyx fragment
def U32.toU16: U32 -> #some(U16) | #outOfRange
```

## `U32.toU64`

```nanyx fragment
def U32.toU64: U32 -> U64
```

## `U32.toFloat`

```nanyx fragment
def U32.toFloat: U32 -> float
```

## `U32.toF32`

```nanyx fragment
def U32.toF32: U32 -> F32
```

## `U64.toInt`

```nanyx fragment
def U64.toInt: U64 -> #some(int) | #outOfRange
```

## `U64.toI8`

```nanyx fragment
def U64.toI8: U64 -> #some(I8) | #outOfRange
```

## `U64.toI16`

```nanyx fragment
def U64.toI16: U64 -> #some(I16) | #outOfRange
```

## `U64.toI32`

```nanyx fragment
def U64.toI32: U64 -> #some(I32) | #outOfRange
```

## `U64.toU8`

```nanyx fragment
def U64.toU8: U64 -> #some(U8) | #outOfRange
```

## `U64.toU16`

```nanyx fragment
def U64.toU16: U64 -> #some(U16) | #outOfRange
```

## `U64.toU32`

```nanyx fragment
def U64.toU32: U64 -> #some(U32) | #outOfRange
```

## `U64.toFloat`

```nanyx fragment
def U64.toFloat: U64 -> float
```

## `U64.toF32`

```nanyx fragment
def U64.toF32: U64 -> F32
```

## `float.toF32`

```nanyx fragment
def float.toF32: float -> F32
```

`x` as the nearest `F32`, or an infinity past the largest `F32`.

## `F32.toFloat`

```nanyx fragment
def F32.toFloat: F32 -> float
```

`x` as a `float`, which holds every `F32` exactly.

## `float.toInt`

```nanyx fragment
def float.toInt: float, #truncate | #round | #floor | #ceil -> int
```

`x` as an `int`, made whole as `mode` says: `float.toInt(2.5, #round)` is 3. A value that is
NaN, infinite or past the range of `int` stops the program; see [`float.tryToInt`](#floattrytoint).

## `float.tryToInt`

```nanyx fragment
def float.tryToInt: float, #truncate | #round | #floor | #ceil -> #some(int) | #outOfRange
```

`x` as an `int`, made whole as `mode` says, or `#outOfRange` when it is NaN, infinite or past
the range of `int`.

## `F32.toInt`

```nanyx fragment
def F32.toInt: F32, #truncate | #round | #floor | #ceil -> int
```

`x` as an `int`, as [`float.toInt`](#floattoint) makes it one.

## `F32.tryToInt`

```nanyx fragment
def F32.tryToInt: F32, #truncate | #round | #floor | #ceil -> #some(int) | #outOfRange
```

`x` as an `int`, or `#outOfRange`, as [`float.tryToInt`](#floattrytoint) makes it one.

## `I8.fromString`

```nanyx fragment
def I8.fromString: string -> #some(I8) | #invalidI8
```

The `I8` `s` writes in decimal, or `#invalidI8` for anything else, a number past the range of
`I8` included.

## `I16.fromString`

```nanyx fragment
def I16.fromString: string -> #some(I16) | #invalidI16
```

The `I16` `s` writes in decimal, or `#invalidI16` for anything else, a number past the range of
`I16` included.

## `I32.fromString`

```nanyx fragment
def I32.fromString: string -> #some(I32) | #invalidI32
```

The `I32` `s` writes in decimal, or `#invalidI32` for anything else, a number past the range of
`I32` included.

## `U8.fromString`

```nanyx fragment
def U8.fromString: string -> #some(U8) | #invalidU8
```

The `U8` `s` writes in decimal, or `#invalidU8` for anything else, a leading `-` or a number
past the range of `U8` included.

## `U16.fromString`

```nanyx fragment
def U16.fromString: string -> #some(U16) | #invalidU16
```

The `U16` `s` writes in decimal, or `#invalidU16` for anything else, a leading `-` or a number
past the range of `U16` included.

## `U32.fromString`

```nanyx fragment
def U32.fromString: string -> #some(U32) | #invalidU32
```

The `U32` `s` writes in decimal, or `#invalidU32` for anything else, a leading `-` or a number
past the range of `U32` included.

## `U64.fromString`

```nanyx fragment
def U64.fromString: string -> #some(U64) | #invalidU64
```

The `U64` `s` writes in decimal, with an optional leading `+`, or `#invalidU64` for anything
else, a leading `-` or a number past 18446744073709551615 included. It reads its digits itself,
as the largest `U64`s are past the range of `int`.

## `F32.fromString`

```nanyx fragment
def F32.fromString: string -> #some(F32) | #invalidF32
```

The `F32` nearest the float `s` writes, as `float.fromString` reads it, or `#invalidF32` for
anything else. A number too large for an `F32` is infinity.

<!-- End of the generated reference. -->
