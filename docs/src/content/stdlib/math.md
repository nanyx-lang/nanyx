---
title: "math"
description: "Numeric utility functions"
order: 9
---

The `math` module provides common numeric and floating-point operations.

Functions that work on any numeric type are generic over the `Num(a)` [context](../advanced/contexts): `int` and `float` are its built-in instances, so `abs`, `sign` and `pow` accept either without conversion. `min`, `max`, `minMax` and `clamp` only compare, so they require [`Ord(a)`](../advanced/contexts#orda) and take values of any one type that has an order: numbers, and strings too. The arguments must all be of the same type: `math.max(1, 2.5)` is a type error. Functions that only make sense for floating-point values take `float` directly.

`floor`, `ceil`, `round` and `truncate` turn a `float` into an `int`; `int.toFloat`, in the prelude, goes the other way. A float that is NaN, infinite or out of the range of `int` has no `int` to give, and stops the program.

# pi

```nanyx
math.pi: float
```

The ratio of a circle's circumference to its diameter, as near as a `float` holds it.

```nanyx
math.pi
-- 3.141592653589793

def area: float -> float = { r -> math.pi * r * r }
```

---

# e

```nanyx
math.e: float
```

The base of the natural logarithm, as near as a `float` holds it. `math.exp(1.0)` is exactly `math.e`.

```nanyx
math.e
-- 2.718281828459045
```

---

# abs

```nanyx
math.abs: [Num(a) & Ord(a)] a -> a
```

```nanyx
math.abs(-42)
-- 42
math.abs(-2.5)
-- 2.5
```

---

# sign

```nanyx
math.sign: [Num(a) & Ord(a)] a -> a
```

`-1`, `0` or `1`, of the argument's type, as the value is below zero, zero, or above it. Negative zero is zero, so `math.sign(-0.0)` is `0.0`.

```nanyx
math.sign(-7)
-- -1
math.sign(0)
-- 0
math.sign(2.5)
-- 1.0
```

---

# min

```nanyx
math.min: [Ord(a)] (a, a) -> a
```

```nanyx
math.min(3, 7)
-- 3
math.min(1.5, 2.0)
-- 1.5
math.min("pear", "apple")
-- "apple"
```

---

# max

```nanyx
math.max: [Ord(a)] (a, a) -> a
```

```nanyx
math.max(3, 7)
-- 7
math.max(1.5, 2.0)
-- 2.0
```

---

# minMax

```nanyx
math.minMax: [Ord(a)] (a, a) -> (a, a)
```

Both values in ascending order: the smaller first, then the larger. When they are equal, the first argument comes first. To put more than two values in order, use [`list.sort`](./collections).

```nanyx
math.minMax(7, 3)
-- (3, 7)

def low, high = math.minMax("pear", "apple")
-- low is "apple", high is "pear"
```

---

# clamp

```nanyx
math.clamp: [Ord(a)] (a, min: a, max: a) -> a
```

The value moved into the range from `min` to `max`: `min` if it is below, `max` if it is above.

```nanyx
math.clamp(120, min = 0, max = 100)
-- 100
math.clamp(-0.5, min = 0.0, max = 1.0)
-- 0.0
```

---

# sqrt

```nanyx
math.sqrt: float -> float
```

The square root of a negative number is NaN.

```nanyx
math.sqrt(81.0)
-- 9.0
```

---

# exp

```nanyx
math.exp: float -> float
```

`e ** x`. It is infinity above about 709.8, and `0.0` below about -745.2.

```nanyx
math.exp(0.0)
-- 1.0
math.exp(1.0)
-- 2.718281828459045
```

---

# ln

```nanyx
math.ln: float -> float
```

The natural logarithm: the power `e` is raised to to give the value. It is minus infinity at zero, and NaN below zero.

```nanyx
math.ln(1.0)
-- 0.0
math.ln(math.e)
-- 1.0
math.ln(0.0)
-- -Infinity
math.ln(-1.0)
-- NaN
```

`exp` and `ln` are computed from their series, as WebAssembly has neither: a result can be one unit in the last place from the nearest `float`.

---

# pow

```nanyx
math.pow: [Num(a)] (a, int) -> a
```

`base ** exponent`, which is how it is usually written: `base` multiplied by itself `exponent` times. It is the prelude's `pow`, and works on any number type. A negative exponent is `1 / base ** -exponent`, so on an `int` it truncates towards zero.

```nanyx
math.pow(2, 8)
-- 256

1.5 ** 2
-- 2.25
```

For an exponent that is not a whole number, `float.pow`, in the prelude, takes a `float`:

```nanyx
float.pow: (float, float) -> float
```

```nanyx
float.pow(2.0, 0.5)
-- 1.4142135623730951
```

A whole exponent gives exactly what `**` does; any other is computed as `math.exp(y * math.ln(x))`, so a negative base gives NaN.

---

# floor

```nanyx
math.floor: float -> int
```

The largest whole number not above the value, so a negative number goes away from zero.

```nanyx
math.floor(3.9)
-- 3
math.floor(-3.1)
-- -4
```

---

# ceil

```nanyx
math.ceil: float -> int
```

The smallest whole number not below the value, so a negative number goes towards zero.

```nanyx
math.ceil(3.1)
-- 4
math.ceil(-3.9)
-- -3
```

---

# round

```nanyx
math.round: float -> int
```

The nearest whole number. A value exactly halfway between two is rounded away from zero.

```nanyx
math.round(3.5)
-- 4
math.round(2.5)
-- 3
math.round(-2.5)
-- -3
```

---

# truncate

```nanyx
math.truncate: float -> int
```

The value with its fraction dropped, so a number goes towards zero whatever its sign.

```nanyx
math.truncate(3.9)
-- 3
math.truncate(-3.9)
-- -3
```
