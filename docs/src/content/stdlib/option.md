---
title: "option"
description: "Working with optional values"
order: 7
---

The option helpers work on [**option-like** unions](../advanced/option-like-types). They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

In Nanyx, any union that includes `#some(a)` is considered option-like. There is no `Option(a)` alias; `#some(a) | #none` is just the most common option-like union, and these helpers are written to preserve all non-`#some` tags, whatever they are. Their names end in `Option`, as in `mapOption`, because there is no `Option` type for them to be attached to.

# map

```nanyx
mapOption: ((#some(a) | r), (a -> b)) -> (#some(b) | r)
```

Transforms the value when present.

```nanyx
#some("abc") \mapOption { .length }
-- #some(3)

def value: (#some(string) | #empty) = #some("abc")
value \mapOption { .length }
-- #some(3)
```

---

# bind

```nanyx
bindOption: ((#some(a) | r), (a -> (#some(b) | s))) -> (#some(b) | r | s)
```

Chains option-producing operations.

```nanyx
def a: (#some(string) | #notFound) = #some("42")
def b = a \bindOption(Int.fromString) -- b: (#some(int) | #notFound | #invalidInt)
```

---

# defaultValue

```nanyx
defaultValueOption: ((#some(a) | r), a) -> a
```

Returns the wrapped value or a fallback.

```nanyx
defaultValueOption(#none, 0)
-- 0
```

---

# isSome

```nanyx
isSomeOption: (#some(a) | r) -> bool
```

```nanyx
isSomeOption(#some(10))
-- true
```

---

# isNone

```nanyx
isNoneOption: (#some(a) | r) -> bool
```

```nanyx
isNoneOption(#none)
-- true
```

---

# toResult

```nanyx
toResultOption: ((#some(a) | r), e) -> #some(a) | #error(e)
```

Converts an option to a result with a supplied error value.

```nanyx
toResultOption(#none, "missing user")
-- #error("missing user")
```

---

# mapOr

```nanyx
mapOrOption: ((#some(a) | r), b, (a -> b)) -> b
```

Maps the contained value or returns a default.

```nanyx
mapOrOption(#some("abc"), 0) { .length }
-- 3

mapOrOption(#none, 0) { .length }
-- 0
```

---

# toList

```nanyx
toListOption: (#some(a) | r) -> list(a)
```

The value as a one-element list, or the empty list for any other tag. Spreading it adds an element only when it is present.

```nanyx
toListOption(#some(3))
-- [3]

def identity: #some(string) | #notFound = #notFound
["device", ...(identity \toListOption)]
-- ["device"]
```

---

# values

```nanyx
valuesOption: list(#some(a) | r) -> list(a)
```

The values of the `#some`s in a list, in order. The other tags are dropped.

```nanyx
["1", "x", "3"] \List.map(Int.fromString) \valuesOption
-- [1, 3]
```

---

# mapOrElse

```nanyx
mapOrElseOption: ((#some(a) | r), () -> b, (a -> b)) -> b
```

Maps the contained value or computes a fallback lazily.

```nanyx
mapOrElseOption(#none, { expensiveFallback() }) { .length }
```

For deeper background on option-like unions, see [Option-like types](../advanced/option-like-types).
