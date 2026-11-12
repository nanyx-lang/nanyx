---
title: "option"
description: "Working with optional values"
order: 7
---

The option helpers work on [**option-like** unions](../advanced/option-like-types). They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

In Nanyx, any union that includes `#some(a)` is considered option-like. There is no `Option(a)` alias; `#some(a) | #none` is just the most common option-like union, and these helpers are written to preserve all non-`#some` tags, whatever they are. They are plain functions rather than definitions attached to a type, because there is no `Option` type for them to be attached to, and they are named for the `#some` tag they act on, as in `mapSome`, `someOr` and `somes`.

# map

```nanyx
mapSome: [c] ((#some(a) | r), ([c] a -> b)) -> (#some(b) | r)
```

Transforms the value when present.

```nanyx
#some("abc") \mapSome { .length }
-- #some(3)

def value: (#some(string) | #empty) = #some("abc")
value \mapSome { .length }
-- #some(3)
```

---

# bind

```nanyx
bindSome: [c] ((#some(a) | r), ([c] a -> (#some(b) | r))) -> (#some(b) | r)
```

Chains option-producing operations. The result has the other tags of both steps: `r` stands for all of them, and each step adds its own.

```nanyx
def a: (#some(string) | #notFound) = #some("42")
def b = a \bindSome(int.fromString) -- b: (#some(int) | #notFound | #invalidInt)

match b
  | #some(n) -> "the number {n}"
  | #notFound -> "nothing to read"
  | #invalidInt -> "not a number"
```

A function that is itself generic in both steps cannot say so yet: a union has one rest variable, so `#some(b) | r | s` is not a type ([#680](https://github.com/nanyx-lang/nanyx/issues/680)).

---

# defaultValue

```nanyx
someOr: ((#some(a) | r), a) -> a
```

Returns the wrapped value or a fallback.

```nanyx
someOr(#none, 0)
-- 0
```

---

# isSome

```nanyx
isSome: (#some(a) | r) -> bool
```

```nanyx
isSome(#some(10))
-- true
```

---

# isNone

```nanyx
isNone: (#some(a) | r) -> bool
```

```nanyx
isNone(#none)
-- true
```

---

# toResult

```nanyx
someToResult: ((#some(a) | r), e) -> #some(a) | #error(e)
```

Converts an option to a result with a supplied error value.

```nanyx
someToResult(#none, "missing user")
-- #error("missing user")
```

---

# mapOr

```nanyx
mapSomeOr: [c] ((#some(a) | r), b, ([c] a -> b)) -> b
```

Maps the contained value or returns a default.

```nanyx
mapSomeOr(#some("abc"), 0) { .length }
-- 3

mapSomeOr(#none, 0) { .length }
-- 0
```

---

# toList

```nanyx
someToList: (#some(a) | r) -> list(a)
```

The value as a one-element list, or the empty list for any other tag. Spreading it adds an element only when it is present.

```nanyx
someToList(#some(3))
-- [3]

def identity: #some(string) | #notFound = #notFound
["device", ...(identity \someToList)]
-- ["device"]
```

---

# values

```nanyx
somes: list(#some(a) | r) -> list(a)
```

The values of the `#some`s in a list, in order. The other tags are dropped.

```nanyx
["1", "x", "3"] \list.map(int.fromString) \somes
-- [1, 3]
```

---

# mapOrElse

```nanyx
mapSomeOrElse: [c] ((#some(a) | r), ([c] () -> b), ([c] a -> b)) -> b
```

Maps the contained value or computes a fallback lazily: the fallback is called only when there is no `#some`.

```nanyx
mapSomeOrElse(#none, { expensiveFallback() }) { .length }
```

---

# orElse

```nanyx
orElseSome: [c] ((#some(a) | r), ([c] () -> (#some(a) | s))) -> #some(a) | s
```

Keeps a `#some` as it is; for any other tag, calls the fallback and gives back what it returns. The fallback is called only when it is needed, and the result has its tags, not the first option's.

```nanyx
def cached: #some(string) | #none = #none
cached \orElseSome { lookup(key) }
-- what lookup(key) gives

#some("hit") \orElseSome { lookup(key) }
-- #some("hit"), and lookup is not called
```

For deeper background on option-like unions, see [Option-like types](../advanced/option-like-types).
