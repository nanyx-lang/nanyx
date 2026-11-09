---
title: "math"
description: "Numeric utility functions"
order: 7
---

The `math` module provides common numeric and floating-point operations.

Functions that work on any numeric type are generic over the `Num(a)` [context](../advanced/contexts); `int` and `float` are its built-in instances, so these functions accept either without conversion. Functions that only make sense for floating-point values take `float` directly.

`floor`, `ceil` and `round` turn a `float` into an `int`. A float that is NaN, infinite or out of the range of `int` has no `int` to give, and stops the program.

# abs

```nanyx
math.abs: [Num(a)] a -> a
```

```nanyx
math.abs(-42)
-- 42
```

---

# min

```nanyx
math.min: [Num(a)] (a, a) -> a
```

```nanyx
math.min(3, 7)
-- 3
```

---

# max

```nanyx
math.max: [Num(a)] (a, a) -> a
```

```nanyx
math.max(3, 7)
-- 7
```

---

# clamp

```nanyx
math.clamp: [Num(a)] (a, min: a, max: a) -> a
```

```nanyx
math.clamp(120, min = 0, max = 100)
-- 100
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

```nanyx
math.pow(2, 8)
-- 256
```

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
