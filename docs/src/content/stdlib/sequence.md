---
title: "sequence"
description: "Helpers for sequence processing"
order: 10
---

The `sequence` module provides operations on `seq(a)`, which is always finite. Potentially infinite sequences have the type `gen(a)` and their own attached definitions; `Gen.take` on a `gen` yields a `seq`, which is how an infinite generator is brought into the finite world where `fold` and `toList` are safe to call. Because the two are different types with their own attached definitions, `xs \take(10)` resolves to whichever applies to `xs`.

# map

```nanyx
Sequence.map: (seq(a), (a -> b)) -> seq(b)
```

```nanyx
numbers \Sequence.map { * 2 }
```

---

# filter

```nanyx
Sequence.filter: (seq(a), (a -> bool)) -> seq(a)
```

```nanyx
numbers \Sequence.filter { > 0 }
```

---

# take

```nanyx
Sequence.take: (seq(a), int) -> seq(a)
```

```nanyx
numbers \Sequence.take(10)
```

The equivalent on a potentially infinite generator is attached to `gen` instead, and is the usual way to make a generator finite:

```nanyx
Gen.take: (gen(a), int) -> seq(a)
```

```nanyx
fibonacci \Gen.take(10)  -- fibonacci: gen(int), result: seq(int)
```

---

# skip

```nanyx
Sequence.skip: (seq(a), int) -> seq(a)
```

```nanyx
items \Sequence.skip(5)
```

---

# fold

```nanyx
Sequence.fold: (seq(a), b, ((b, a) -> b)) -> b
```

```nanyx
numbers \Sequence.fold(0) { + }
```

---

# toList

```nanyx
Sequence.toList: seq(a) -> list(a)
```

Materializes a sequence into a strict list.

```nanyx
numbers \Sequence.take(3) \Sequence.toList
-- [first, second, third]
```
