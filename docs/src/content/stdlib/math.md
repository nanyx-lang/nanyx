---
title: math
description: Numeric functions and constants
order: 12
---

The arguments of a function generic over `Num(a)` or `Ord(a)` must all be of the same type: `math.max(1, 2.5)` is a type error. To put more than two values in order, use [`list.sort`](prelude-lists#listsort). `int.toFloat`, in the [prelude](prelude-numbers#inttofloat), turns an `int` into a `float`, and `floor`, `ceil`, `round` and `truncate` go the other way. A float that is NaN, infinite or out of the range of `int` has no `int` to give, and stops the program.

```nanyx
import nanyx/math as math

def area: float -> float = { r ->
  math.pi * r * r
}

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

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Numeric utilities.

[`abs`](#abs), [`sign`](#sign) and [`pow`](#pow) are generic over the `Num(a)` context, so they take an `int` or a
`float`. `abs`, `sign`, [`min`](#min), [`max`](#max), [`minMax`](#minmax) and [`clamp`](#clamp) compare through `Ord(a)`, so the
last four take a value of any type that has an order, a `string` among them. [`sqrt`](#sqrt), [`exp`](#exp),
[`ln`](#ln), [`log10`](#log10), [`log2`](#log2), [`floor`](#floor), [`ceil`](#ceil), [`round`](#round) and [`truncate`](#truncate) take a `float`, and so
do the trigonometric functions, [`sin`](#sin), [`cos`](#cos), [`tan`](#tan), [`asin`](#asin), [`acos`](#acos), [`atan`](#atan) and
[`atan2`](#atan2), whose angles are in radians. [`pi`](#pi), [`e`](#e),
[`infinity`](#infinity) and [`nan`](#nan) are constants, and [`isNaN`](#isnan), [`isInfinite`](#isinfinite) and [`isFinite`](#isfinite) tell the
special floats from the others.

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

## `infinity`

```nanyx fragment
def infinity: float
```

Positive infinity, the `float` above every other: what `1.0 / 0.0` gives, and `exp` gives past
about 709.8. Minus infinity is `-math.infinity`. It is written `Infinity`.

## `nan`

```nanyx fragment
def nan: float
```

Not a number: the `float` that `0.0 / 0.0` and `sqrt(-1.0)` give. It is not equal to anything,
itself included, so test for it with [`isNaN`](#isnan), not `==`. It is written `NaN`.

## `isNaN`

```nanyx fragment
def isNaN: float -> bool
```

Whether `x` is NaN, as [`nan`](#nan) is: the one `float` not equal to itself.

## `isInfinite`

```nanyx fragment
def isInfinite: float -> bool
```

Whether `x` is infinity or minus infinity.

## `isFinite`

```nanyx fragment
def isFinite: float -> bool
```

Whether `x` is an ordinary number: neither NaN nor infinite.

## `abs`

```nanyx fragment
def abs: [Num(a), Ord(a)] a -> a
```

`n` without its sign. A value that is neither below nor above `0` is `zero`, so `abs(-0.0)` is `0.0`.

## `sign`

```nanyx fragment
def sign: [Num(a), Ord(a)] a -> a
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
def clamp: [Ord(a)] (a, min: a, max: a) -> a
```

`n` moved into the range from `min` to `max`: `min` if it is below, `max` if it is above.

## `pow`

```nanyx fragment
def pow: [Num(a)] a, int -> a
```

`base ** exponent`: `base` multiplied by itself `exponent` times, for any number type. A negative
exponent is `1 / base ** -exponent` on a `float`, and stops the program on an integer type, which
can't hold the result. For an exponent that is not a whole number, see [`float.pow`](prelude-numbers#floatpow).

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

## `log10`

```nanyx fragment
def log10: float -> float
```

The base-10 logarithm of `x`, the power 10 is raised to to give `x`: exact at a power of ten,
so `log10(1000.0)` is `3.0`. It is minus infinity at zero, and NaN below it.

## `log2`

```nanyx fragment
def log2: float -> float
```

The base-2 logarithm of `x`, the power 2 is raised to to give `x`: exact at a power of two, so
`log2(1024.0)` is `10.0`. It is minus infinity at zero, and NaN below it.

## `sin`

```nanyx fragment
def sin: float -> float
```

The sine of `x`, an angle in radians: `sin(pi / 6.0)` is `0.5`. NaN for an infinite `x`.

## `cos`

```nanyx fragment
def cos: float -> float
```

The cosine of `x`, an angle in radians: `cos(pi)` is `-1.0`. NaN for an infinite `x`.

## `tan`

```nanyx fragment
def tan: float -> float
```

The tangent of `x`, an angle in radians, its sine over its cosine: `tan(pi / 4.0)` is `1.0`. NaN
for an infinite `x`.

## `atan`

```nanyx fragment
def atan: float -> float
```

The arctangent of `x`, the angle in (-pi/2, pi/2) whose tangent is `x`: `atan(1.0)` is `pi / 4.0`.

## `asin`

```nanyx fragment
def asin: float -> float
```

The arcsine of `x`, the angle in [-pi/2, pi/2] whose sine is `x`: NaN outside [-1, 1].

## `acos`

```nanyx fragment
def acos: float -> float
```

The arccosine of `x`, the angle in [0, pi] whose cosine is `x`: NaN outside [-1, 1].

## `atan2`

```nanyx fragment
def atan2: float, float -> float
```

The angle of the point (`x`, `y`) from the positive x axis, in (-pi, pi]. It takes `y` first, then
`x`, as C, .NET, Rust and Python do: `atan2(1.0, -1.0)` is `3.0 * pi / 4.0`. Unlike `atan(y / x)`,
it tells the four quarters apart, and is defined where `x` is zero.

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
