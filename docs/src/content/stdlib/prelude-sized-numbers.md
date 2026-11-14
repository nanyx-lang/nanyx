---
title: "prelude: sized numbers"
description: Arithmetic, comparison, hashing and text for I8 to U64 and F32
order: 5
---

The definitions attached to the [sized number types](../advanced/sized-numbers), `I8`, `I16`, `I32`, `U8`, `U16`, `U32`, `U64` and `F32`, that give each the operators and contexts `int` and `float` have, and the wrapping arithmetic of `int` and the sized integers. They are in the [prelude](prelude), so no import is needed. Converting between them is on the [conversions](prelude-conversions) page.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `I8.zero`

```nanyx fragment
def I8.zero: I8
```

## `` I8.`+` ``

```nanyx fragment
def I8.`+`: I8, I8 -> I8
```

## `` I8.`-` ``

```nanyx fragment
def I8.`-`: I8, I8 -> I8
```

## `I8.negate`

```nanyx fragment
def I8.negate: I8 -> I8
```

## `` I8.`*` ``

```nanyx fragment
def I8.`*`: I8, I8 -> I8
```

## `` I8.`/` ``

```nanyx fragment
def I8.`/`: I8, I8 -> I8
```

## `` I8.`%` ``

```nanyx fragment
def I8.`%`: I8, I8 -> I8
```

## `I8.fromInt`

```nanyx fragment
def I8.fromInt: int -> I8
```

## `I8.compare`

```nanyx fragment
def I8.compare: I8, I8 -> #less | #equal | #greater
```

## `I8.hash`

```nanyx fragment
def I8.hash: I8 -> int
```

## `I8.toString`

```nanyx fragment
def I8.toString: I8 -> string
```

## `I16.zero`

```nanyx fragment
def I16.zero: I16
```

## `` I16.`+` ``

```nanyx fragment
def I16.`+`: I16, I16 -> I16
```

## `` I16.`-` ``

```nanyx fragment
def I16.`-`: I16, I16 -> I16
```

## `I16.negate`

```nanyx fragment
def I16.negate: I16 -> I16
```

## `` I16.`*` ``

```nanyx fragment
def I16.`*`: I16, I16 -> I16
```

## `` I16.`/` ``

```nanyx fragment
def I16.`/`: I16, I16 -> I16
```

## `` I16.`%` ``

```nanyx fragment
def I16.`%`: I16, I16 -> I16
```

## `I16.fromInt`

```nanyx fragment
def I16.fromInt: int -> I16
```

## `I16.compare`

```nanyx fragment
def I16.compare: I16, I16 -> #less | #equal | #greater
```

## `I16.hash`

```nanyx fragment
def I16.hash: I16 -> int
```

## `I16.toString`

```nanyx fragment
def I16.toString: I16 -> string
```

## `I32.zero`

```nanyx fragment
def I32.zero: I32
```

## `` I32.`+` ``

```nanyx fragment
def I32.`+`: I32, I32 -> I32
```

## `` I32.`-` ``

```nanyx fragment
def I32.`-`: I32, I32 -> I32
```

## `I32.negate`

```nanyx fragment
def I32.negate: I32 -> I32
```

## `` I32.`*` ``

```nanyx fragment
def I32.`*`: I32, I32 -> I32
```

## `` I32.`/` ``

```nanyx fragment
def I32.`/`: I32, I32 -> I32
```

## `` I32.`%` ``

```nanyx fragment
def I32.`%`: I32, I32 -> I32
```

## `I32.fromInt`

```nanyx fragment
def I32.fromInt: int -> I32
```

## `I32.compare`

```nanyx fragment
def I32.compare: I32, I32 -> #less | #equal | #greater
```

## `I32.hash`

```nanyx fragment
def I32.hash: I32 -> int
```

## `I32.toString`

```nanyx fragment
def I32.toString: I32 -> string
```

## `U8.zero`

```nanyx fragment
def U8.zero: U8
```

## `` U8.`+` ``

```nanyx fragment
def U8.`+`: U8, U8 -> U8
```

## `` U8.`-` ``

```nanyx fragment
def U8.`-`: U8, U8 -> U8
```

## `U8.negate`

```nanyx fragment
def U8.negate: U8 -> U8
```

## `` U8.`*` ``

```nanyx fragment
def U8.`*`: U8, U8 -> U8
```

## `` U8.`/` ``

```nanyx fragment
def U8.`/`: U8, U8 -> U8
```

## `` U8.`%` ``

```nanyx fragment
def U8.`%`: U8, U8 -> U8
```

## `U8.fromInt`

```nanyx fragment
def U8.fromInt: int -> U8
```

## `U8.compare`

```nanyx fragment
def U8.compare: U8, U8 -> #less | #equal | #greater
```

## `U8.hash`

```nanyx fragment
def U8.hash: U8 -> int
```

## `U8.toString`

```nanyx fragment
def U8.toString: U8 -> string
```

## `U16.zero`

```nanyx fragment
def U16.zero: U16
```

## `` U16.`+` ``

```nanyx fragment
def U16.`+`: U16, U16 -> U16
```

## `` U16.`-` ``

```nanyx fragment
def U16.`-`: U16, U16 -> U16
```

## `U16.negate`

```nanyx fragment
def U16.negate: U16 -> U16
```

## `` U16.`*` ``

```nanyx fragment
def U16.`*`: U16, U16 -> U16
```

## `` U16.`/` ``

```nanyx fragment
def U16.`/`: U16, U16 -> U16
```

## `` U16.`%` ``

```nanyx fragment
def U16.`%`: U16, U16 -> U16
```

## `U16.fromInt`

```nanyx fragment
def U16.fromInt: int -> U16
```

## `U16.compare`

```nanyx fragment
def U16.compare: U16, U16 -> #less | #equal | #greater
```

## `U16.hash`

```nanyx fragment
def U16.hash: U16 -> int
```

## `U16.toString`

```nanyx fragment
def U16.toString: U16 -> string
```

## `U32.zero`

```nanyx fragment
def U32.zero: U32
```

## `` U32.`+` ``

```nanyx fragment
def U32.`+`: U32, U32 -> U32
```

## `` U32.`-` ``

```nanyx fragment
def U32.`-`: U32, U32 -> U32
```

## `U32.negate`

```nanyx fragment
def U32.negate: U32 -> U32
```

## `` U32.`*` ``

```nanyx fragment
def U32.`*`: U32, U32 -> U32
```

## `` U32.`/` ``

```nanyx fragment
def U32.`/`: U32, U32 -> U32
```

## `` U32.`%` ``

```nanyx fragment
def U32.`%`: U32, U32 -> U32
```

## `U32.fromInt`

```nanyx fragment
def U32.fromInt: int -> U32
```

## `U32.compare`

```nanyx fragment
def U32.compare: U32, U32 -> #less | #equal | #greater
```

## `U32.hash`

```nanyx fragment
def U32.hash: U32 -> int
```

## `U32.toString`

```nanyx fragment
def U32.toString: U32 -> string
```

## `U64.zero`

```nanyx fragment
def U64.zero: U64
```

## `` U64.`+` ``

```nanyx fragment
def U64.`+`: U64, U64 -> U64
```

## `` U64.`-` ``

```nanyx fragment
def U64.`-`: U64, U64 -> U64
```

## `U64.negate`

```nanyx fragment
def U64.negate: U64 -> U64
```

## `` U64.`*` ``

```nanyx fragment
def U64.`*`: U64, U64 -> U64
```

## `` U64.`/` ``

```nanyx fragment
def U64.`/`: U64, U64 -> U64
```

## `` U64.`%` ``

```nanyx fragment
def U64.`%`: U64, U64 -> U64
```

## `U64.fromInt`

```nanyx fragment
def U64.fromInt: int -> U64
```

## `U64.compare`

```nanyx fragment
def U64.compare: U64, U64 -> #less | #equal | #greater
```

## `U64.hash`

```nanyx fragment
def U64.hash: U64 -> int
```

## `U64.toString`

```nanyx fragment
def U64.toString: U64 -> string
```

## `F32.zero`

```nanyx fragment
def F32.zero: F32
```

## `` F32.`+` ``

```nanyx fragment
def F32.`+`: F32, F32 -> F32
```

## `` F32.`-` ``

```nanyx fragment
def F32.`-`: F32, F32 -> F32
```

## `F32.negate`

```nanyx fragment
def F32.negate: F32 -> F32
```

## `` F32.`*` ``

```nanyx fragment
def F32.`*`: F32, F32 -> F32
```

## `` F32.`/` ``

```nanyx fragment
def F32.`/`: F32, F32 -> F32
```

## `` F32.`%` ``

```nanyx fragment
def F32.`%`: F32, F32 -> F32
```

## `F32.fromInt`

```nanyx fragment
def F32.fromInt: int -> F32
```

## `F32.compare`

```nanyx fragment
def F32.compare: F32, F32 -> #less | #equal | #greater
```

## `F32.hash`

```nanyx fragment
def F32.hash: F32 -> int
```

## `F32.toString`

```nanyx fragment
def F32.toString: F32 -> string
```

## `int.wrappingAdd`

```nanyx fragment
def int.wrappingAdd: int, int -> int
```

`x` plus `y`, wrapping around past the largest or smallest `int`: `int.wrappingAdd(9_223_372_036_854_775_807, 1)`
is the smallest `int`. Each integer type has its own, `I32.wrappingAdd` and the rest.

## `int.wrappingSub`

```nanyx fragment
def int.wrappingSub: int, int -> int
```

`x` minus `y`, wrapping around as [`int.wrappingAdd`](#intwrappingadd) does.

## `int.wrappingMul`

```nanyx fragment
def int.wrappingMul: int, int -> int
```

`x` times `y`, wrapping around as [`int.wrappingAdd`](#intwrappingadd) does.

## `I8.wrappingAdd`

```nanyx fragment
def I8.wrappingAdd: I8, I8 -> I8
```

## `I8.wrappingSub`

```nanyx fragment
def I8.wrappingSub: I8, I8 -> I8
```

## `I8.wrappingMul`

```nanyx fragment
def I8.wrappingMul: I8, I8 -> I8
```

## `I16.wrappingAdd`

```nanyx fragment
def I16.wrappingAdd: I16, I16 -> I16
```

## `I16.wrappingSub`

```nanyx fragment
def I16.wrappingSub: I16, I16 -> I16
```

## `I16.wrappingMul`

```nanyx fragment
def I16.wrappingMul: I16, I16 -> I16
```

## `I32.wrappingAdd`

```nanyx fragment
def I32.wrappingAdd: I32, I32 -> I32
```

## `I32.wrappingSub`

```nanyx fragment
def I32.wrappingSub: I32, I32 -> I32
```

## `I32.wrappingMul`

```nanyx fragment
def I32.wrappingMul: I32, I32 -> I32
```

## `U8.wrappingAdd`

```nanyx fragment
def U8.wrappingAdd: U8, U8 -> U8
```

## `U8.wrappingSub`

```nanyx fragment
def U8.wrappingSub: U8, U8 -> U8
```

## `U8.wrappingMul`

```nanyx fragment
def U8.wrappingMul: U8, U8 -> U8
```

## `U16.wrappingAdd`

```nanyx fragment
def U16.wrappingAdd: U16, U16 -> U16
```

## `U16.wrappingSub`

```nanyx fragment
def U16.wrappingSub: U16, U16 -> U16
```

## `U16.wrappingMul`

```nanyx fragment
def U16.wrappingMul: U16, U16 -> U16
```

## `U32.wrappingAdd`

```nanyx fragment
def U32.wrappingAdd: U32, U32 -> U32
```

## `U32.wrappingSub`

```nanyx fragment
def U32.wrappingSub: U32, U32 -> U32
```

## `U32.wrappingMul`

```nanyx fragment
def U32.wrappingMul: U32, U32 -> U32
```

## `U64.wrappingAdd`

```nanyx fragment
def U64.wrappingAdd: U64, U64 -> U64
```

## `U64.wrappingSub`

```nanyx fragment
def U64.wrappingSub: U64, U64 -> U64
```

## `U64.wrappingMul`

```nanyx fragment
def U64.wrappingMul: U64, U64 -> U64
```

<!-- End of the generated reference. -->
