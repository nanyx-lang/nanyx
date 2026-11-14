---
title: math
description: Numeric functions and constants
order: 12
---

The arguments of a function generic over `Num(a)` or `Ord(a)` must all be of the same type: `math.max(1, 2.5)` is a type error. To put more than two values in order, use [`list.sort`](prelude-lists#listsort). `int.toFloat`, in the [prelude](prelude-numbers#inttofloat), turns an `int` into a `float`, and `floor`, `ceil`, `round` and `truncate` go the other way. A float that is NaN, infinite or out of the range of `int` has no `int` to give, and stops the program.

```nanyx
import nanyx/console
import nanyx/math as math

def area: float -> float = { r -> math.pi * r * r }

println(math.abs(-42))
println(math.clamp(120, min = 0, max = 100))
println(math.round(area(2.0)))
println(math.min("pear", "apple"))
-- 42
-- 100
-- 13
-- apple
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

Numeric utilities.

[`abs`](#abs), [`sign`](#sign) and [`pow`](#pow) are generic over the `Num(a)` context, so they take an `int` or a
`float`. `abs`, `sign`, [`min`](#min), [`max`](#max), [`minMax`](#minmax) and [`clamp`](#clamp) compare through `Ord(a)`, so the
last four take a value of any type that has an order, a `string` among them. [`sqrt`](#sqrt), [`exp`](#exp),
[`ln`](#ln), [`floor`](#floor), [`ceil`](#ceil), [`round`](#round) and [`truncate`](#truncate) take a `float`. [`pi`](#pi) and [`e`](#e) are
constants.

Import it with `import nanyx/math as math` to call `math.abs(x)` as the docs do, or unqualified to
call `abs(x)`.

## `pi`

```nanyx fragment
def pi: float
```

The ratio of a circle's circumference to its diameter, as near as a `float` holds it.

## `e`

```nanyx fragment
def e: float
```

The base of the natural logarithm, as near as a `float` holds it, so `exp(1.0)` is `e`.

## `abs`

```nanyx fragment
def abs: [Num(a) & Ord(a)] a -> a
```

`n` without its sign. A value that is neither below nor above `0` is `zero`, so `abs(-0.0)` is `0.0`.

## `sign`

```nanyx fragment
def sign: [Num(a) & Ord(a)] a -> a
```

`-1`, `0` or `1`, of the type of `n`, as `n` is below, neither below nor above, or above `zero`:
`sign(-2.5)` is `-1.0` and `sign(-0.0)` is `0.0`.

## `min`

```nanyx fragment
def min: [Ord(a)] a, a -> a
```

The smaller of `a` and `b`.

## `max`

```nanyx fragment
def max: [Ord(a)] a, a -> a
```

The larger of `a` and `b`.

## `minMax`

```nanyx fragment
def minMax: [Ord(a)] a, a -> (a, a)
```

`a` and `b` in ascending order: the smaller first, then the larger. When they are equal, `a` comes
first.

## `clamp`

```nanyx fragment
def clamp: [Ord(a)] (a, max: a, min: a) -> a
```

`n` moved into the range from `min` to `max`: `min` if it is below, `max` if it is above.

## `pow`

```nanyx fragment
def pow: [Num(a)] a, int -> a
```

`base ** exponent`: `base` multiplied by itself `exponent` times, for any number type. A negative
exponent is `1 / base ** -exponent`, so on an `int` it truncates towards zero. For an exponent
that is not a whole number, see [`float.pow`](prelude-numbers#floatpow).

## `sqrt`

```nanyx fragment
def sqrt: float -> float
```

The square root of `x` (NaN when `x` is negative).

## `exp`

```nanyx fragment
def exp: float -> float
```

`e ** x`: `exp(0.0)` is `1.0` and `exp(1.0)` is `e`. It is infinity above about 709.8 and `0.0` below
about -745.2.

## `ln`

```nanyx fragment
def ln: float -> float
```

The natural logarithm of `x`, the power `e` is raised to to give `x`: `ln(e)` is `1.0` and `ln(1.0)` is
`0.0`. It is minus infinity at zero, and NaN below it. `exp` and `ln` are computed from their series,
as WebAssembly has neither, so a result can be one unit in the last place from the nearest `float`.

## `floor`

```nanyx fragment
def floor: float -> int
```

The largest whole number not above `x`: `floor(3.9)` is 3 and `floor(-3.1)` is -4.

## `ceil`

```nanyx fragment
def ceil: float -> int
```

The smallest whole number not below `x`: `ceil(3.1)` is 4 and `ceil(-3.9)` is -3.

## `round`

```nanyx fragment
def round: float -> int
```

The whole number nearest `x`, with halves rounded away from zero: `round(2.5)` is 3 and `round(-2.5)` is -3.

## `truncate`

```nanyx fragment
def truncate: float -> int
```

`x` with its fraction dropped, towards zero: `truncate(3.9)` is 3 and `truncate(-3.9)` is -3.

<!-- End of the generated reference. -->
