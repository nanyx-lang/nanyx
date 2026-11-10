---
title: "option"
description: "Working with optional values"
order: 5
---

The `Option` helpers work on [**option-like** unions](../advanced/option-like-types). They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

In Nanyx, any union that includes `#some(a)` is considered option-like. There is no `Option(a)` alias; `#some(a) | #none` is just the most common option-like union, and these helpers are written to preserve all non-`#some` tags, whatever they are.

# map

```nanyx
Option.map: ((#some(a) | r), (a -> b)) -> (#some(b) | r)
```

Transforms the value when present.

```nanyx
#some("abc") \Option.map { .length }
-- #some(3)

def value: (#some(string) | #empty) = #some("abc")
value \Option.map { .length }
-- #some(3)
```

---

# bind

```nanyx
Option.bind: ((#some(a) | r), (a -> (#some(b) | s))) -> (#some(b) | r | s)
```

Chains option-producing operations.

```nanyx
def a: (#some(string) | #notFound) = #some("42")
def b = a \Option.bind(Int.fromString) -- b: (#some(int) | #notFound | #invalidInt)
```

---

# defaultValue

```nanyx
Option.defaultValue: ((#some(a) | r), a) -> a
```

Returns the wrapped value or a fallback.

```nanyx
Option.defaultValue(#none, 0)
-- 0
```

---

# isSome

```nanyx
Option.isSome: (#some(a) | r) -> bool
```

```nanyx
Option.isSome(#some(10))
-- true
```

---

# isNone

```nanyx
Option.isNone: (#some(a) | r) -> bool
```

```nanyx
Option.isNone(#none)
-- true
```

---

# toResult

```nanyx
Option.toResult: ((#some(a) | r), e) -> #some(a) | #error(e)
```

Converts an option to a result with a supplied error value.

```nanyx
Option.toResult(#none, "missing user")
-- #error("missing user")
```

---

# mapOr

```nanyx
Option.mapOr: ((#some(a) | r), b, (a -> b)) -> b
```

Maps the contained value or returns a default.

```nanyx
Option.mapOr(#some("abc"), 0) { .length }
-- 3

Option.mapOr(#none, 0) { .length }
-- 0
```

---

# toList

```nanyx
Option.toList: (#some(a) | r) -> list(a)
```

The value as a one-element list, or the empty list for any other tag. Spreading it adds an element only when it is present.

```nanyx
Option.toList(#some(3))
-- [3]

def identity: #some(string) | #notFound = #notFound
["device", ...(identity \Option.toList)]
-- ["device"]
```

---

# values

```nanyx
Option.values: list(#some(a) | r) -> list(a)
```

The values of the `#some`s in a list, in order. The other tags are dropped.

```nanyx
["1", "x", "3"] \List.map(Int.fromString) \Option.values
-- [1, 3]
```

---

# mapOrElse

```nanyx
Option.mapOrElse: ((#some(a) | r), () -> b, (a -> b)) -> b
```

Maps the contained value or computes a fallback lazily.

```nanyx
Option.mapOrElse(#none, { expensiveFallback() }) { .length }
```

For deeper background on option-like unions, see [Option-like types](../advanced/option-like-types).
