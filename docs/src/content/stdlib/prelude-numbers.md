---
title: "prelude: numbers"
description: Arithmetic on int and float, powers, and reading numbers from text
order: 4
---

The number contexts, `Add(a)` and `Num(a)`, the arithmetic attached to `int` and `float` that the operators read through them, powers, and reading numbers from text. They are in the [prelude](prelude), so no import is needed. [`nanyx/math`](math) has more functions on numbers.

# Converting between numbers

Arithmetic doesn't mix `int` and `float`: `n * 1.5` is a type error when `n` is an `int`. [`int.toFloat`](#inttofloat) turns an `int` into a `float`, and never fails:

```nanyx fragment
def n = 3
def x = int.toFloat(n) * 1.5     -- 4.5
def half = (7 \toFloat) / 2.0    -- 3.5
```

To turn a `float` back into an `int`, choose how to drop the fraction with `floor`, `ceil`, `round` or `truncate` from [`nanyx/math`](math), or with [`float.toInt`](prelude-conversions#floattoint). Conversions to and from the sized number types are on the [conversions](prelude-conversions) page.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `int.fromString`

```nanyx fragment
def int.fromString: string -> #some(int) | #invalidInt
```

The integer `s` writes in decimal, with an optional leading `-` or `+`, or `#invalidInt` for
anything else (an empty string, a sign alone, spaces, other characters, or a number past the range
of `int`).

## `int.toFloat`

```nanyx fragment
def int.toFloat: int -> float
```

`n` as a float: `int.toFloat(3)` is 3.0. Every `int` has an exact `float`. To go back, see
`floor`, `ceil`, `round` and `truncate` in `nanyx/math`.

## `Add`

```nanyx fragment
context Add(a) = (
  zero: a
  `+`: a, a -> a
  `-`: a, a -> a
  negate: a -> a
)
```

The values of type `a` that add and subtract. `x + y`, `x - y` and `-x` are read through it, and
so is `0` beside a value of type `a` in a function whose type requires it:
`def total: [Add(a)] list(a) -> a`. A type gets one by attaching the four members,
`def Money.zero = ...`, `` def Money.`+` = ... ``, and so on.

## `Num`

```nanyx fragment
context Num(a) = (
  zero: a
  `+`: a, a -> a
  `-`: a, a -> a
  negate: a -> a
  `*`: a, a -> a
  `/`: a, a -> a
  `%`: a, a -> a
  fromInt: int -> a
)
```

The numbers of type `a`: [`Add`](#add)'s members, and `x * y`, `x / y` and `x % y`. In a function whose
type requires it, any other integer literal beside a value of type `a` is `fromInt` of it, so
`def square: [Num(a)] a -> a = { x -> x * x }` and `{ x -> x * 2 }` work at `int` and `float`.
A function that requires `Num(a)` can call one that requires `Add(a)`: a `Num` has every member
an `Add` has. On an integer type `/` truncates and `%` takes the sign of `x`; on `float` they are
IEEE 754's.

## `int.zero`

```nanyx fragment
def int.zero: int
```

The `int` that adds nothing.

## `` int.`+` ``

```nanyx fragment
def int.`+`: int, int -> int
```

`x` plus `y`.

## `` int.`-` ``

```nanyx fragment
def int.`-`: int, int -> int
```

`x` minus `y`.

## `int.negate`

```nanyx fragment
def int.negate: int -> int
```

`n` with its sign changed.

## `` int.`*` ``

```nanyx fragment
def int.`*`: int, int -> int
```

`x` times `y`.

## `` int.`/` ``

```nanyx fragment
def int.`/`: int, int -> int
```

`x` divided by `y`, truncated towards zero: `7 / 2` is 3 and `-7 / 2` is -3.

## `` int.`%` ``

```nanyx fragment
def int.`%`: int, int -> int
```

The remainder of `x` divided by `y`, with the sign of `x`: `-7 % 2` is -1.

## `int.fromInt`

```nanyx fragment
def int.fromInt: int -> int
```

`n` itself.

## `float.zero`

```nanyx fragment
def float.zero: float
```

The `float` that adds nothing.

## `` float.`+` ``

```nanyx fragment
def float.`+`: float, float -> float
```

`x` plus `y`.

## `` float.`-` ``

```nanyx fragment
def float.`-`: float, float -> float
```

`x` minus `y`.

## `float.negate`

```nanyx fragment
def float.negate: float -> float
```

`x` with its sign changed: `float.negate(0.0)` is negative zero.

## `` float.`*` ``

```nanyx fragment
def float.`*`: float, float -> float
```

`x` times `y`.

## `` float.`/` ``

```nanyx fragment
def float.`/`: float, float -> float
```

`x` divided by `y`.

## `` float.`%` ``

```nanyx fragment
def float.`%`: float, float -> float
```

The remainder of `x` divided by `y`, truncated as C's `fmod` is: `7.5 % 2.0` is 1.5.

## `float.fromInt`

```nanyx fragment
def float.fromInt: int -> float
```

`n` as a float, as [`int.toFloat`](#inttofloat) gives it.

## `pow`

```nanyx fragment
def pow: [Num(a)] a, int -> a
```

`base ** exponent`, as a function that can be passed around.

## `float.pow`

```nanyx fragment
def float.pow: float, float -> float
```

`x` to the power `y`, which need not be a whole number: `float.pow(2.0, 0.5)` is the square root
of 2. A whole `y` is `x ** y`, exactly; any other is `e ** (y * ln(x))`, so a negative `x` gives
NaN.

## `float.fromString`

```nanyx fragment
def float.fromString: string -> #some(float) | #invalidFloat
```

The float `s` writes in decimal, or `#invalidFloat` for anything else (an empty string, a sign
alone, spaces, other characters, `"1.2.3"`). It reads an optional leading `-` or `+`, digits with
an optional `.` among them (`"3"`, `"0.25"`, `".5"` and `"5."` are all read), and an optional
exponent, `e` or `E` then an integer: `"1.0e-7"`. `NaN`, `Infinity` and `-Infinity` are read as
[`float.toString`](prelude-contexts#floattostring) writes them. A number too large for a float is infinity.

The result is the nearest float to the decimal when it has at most 15 significant digits and an
exponent, once the point is moved to the end, between -22 and 22, which covers most text written
by hand. Otherwise it is not correctly rounded and may be a unit in the last place out.

<!-- End of the generated reference. -->
