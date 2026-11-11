---
title: "math"
description: "Numeric utility functions"
order: 9
---

The `math` module provides common numeric and floating-point operations.

Functions that work on any numeric type are generic over the `Num(a)` [context](../advanced/contexts): `int` and `float` are its built-in instances, so `abs` and `pow` accept either without conversion. `min`, `max` and `clamp` only compare, so they require [`Ord(a)`](../advanced/contexts#orda) and take values of any one type that has an order: numbers, and strings too. The arguments must all be of the same type: `math.max(1, 2.5)` is a type error. Functions that only make sense for floating-point values take `float` directly.

`floor`, `ceil`, `round` and `truncate` turn a `float` into an `int`; `Int.toFloat`, in the prelude, goes the other way. A float that is NaN, infinite or out of the range of `int` has no `int` to give, and stops the program.

# abs

```nanyx
math.abs: [Num(a) + Ord(a)] a -> a
```

```nanyx
math.abs(-42)
-- 42
math.abs(-2.5)
-- 2.5
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

For an exponent that is not a whole number, `Float.pow`, in the prelude, takes a `float`:

```nanyx
Float.pow: (float, float) -> float
```

```nanyx
Float.pow(2.0, 0.5)
-- 1.414213562373095
```

A whole exponent gives exactly what `**` does; any other is computed as `e ** (y * ln(x))`, so a negative base gives NaN.

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
