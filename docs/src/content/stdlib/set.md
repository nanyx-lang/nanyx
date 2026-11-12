---
title: set
description: Sets of values, each held once
order: 5
---

A `set(a)` holds each of its elements once. There is no set literal yet: a set is made from a list with `set.fromList`, or from `set.empty` with `set.insert`. `for s | x -> ...` walks a set's elements.

```nanyx
def seen = set.fromList([3, 1, 3, 2])

set.contains(seen, 2)   -- true
set.count(seen)         -- 3
```

A set's elements need an [`Eq`](../advanced/contexts#eqa) and a [`Hash`](../advanced/contexts#hasha) of their type, as a [map](map)'s keys do, so every function that looks one up requires `[Eq(a) & Hash(a)]`. Two sets cannot be compared with `==` yet ([#618](https://github.com/nanyx-lang/nanyx/issues/618)).

These functions are [attached definitions](../advanced/attached-definitions) on `set`, so `s \insert(x)` resolves to `set.insert` when `s` is a set, and are in the [prelude](../tour/modules#modules-in-the-standard-library). Sets are immutable: every function gives back a new set. For now a set keeps its elements in the order they were first added, and `contains` takes time in proportion to its size (see [Maps and sets](../work-in-progress/maps-and-sets)).

## empty

```nanyx
set.empty: set(a)
```

The set with no elements.

---

## fromList

```nanyx
set.fromList: [Eq(a) & Hash(a)] list(a) -> set(a)
```

The set of the elements of a list, each once, in the order they first appear.

---

## toList

```nanyx
set.toList: set(a) -> list(a)
```

The elements of a set, in the order they were first added.

```nanyx
set.fromList([3, 1, 3, 2]) \set.toList
-- [3, 1, 2]
```

---

## contains

```nanyx
set.contains: [Eq(a) & Hash(a)] (set(a), a) -> bool
```

Whether a set holds an element.

---

## insert

```nanyx
set.insert: [Eq(a) & Hash(a)] (set(a), a) -> set(a)
```

The set with an element added at the end. A set that holds the element already is given back as it is.

---

## remove

```nanyx
set.remove: [Eq(a) & Hash(a)] (set(a), a) -> set(a)
```

The set without an element. A set that does not hold it is given back as it is.

---

## count

```nanyx
set.count: set(a) -> int
```

The number of elements in a set.

---

## isEmpty

```nanyx
set.isEmpty: set(a) -> bool
```

Whether a set has no elements.

```nanyx
set.isEmpty(set.empty)            -- true
set.isEmpty(set.fromList([1]))    -- false
```

---

## fold

```nanyx
set.fold: (set(a), b, ((b, a) -> b)) -> b
```

Combines a starting value with each element of a set in turn, in the order of `set.toList`.

```nanyx
set.fromList([3, 1, 2]) \set.fold(0, { total, n -> total + n })
-- 6
```

---

## map

```nanyx
set.map: [Eq(b) & Hash(b)] (set(a), (a -> b)) -> set(b)
```

The set of what a function makes of each element. Elements the function makes equal are kept once, where the first of them was.

```nanyx
set.fromList([1, 2, 3]) \set.map { % 2 } \set.toList
-- [1, 0]
```

---

## filter

```nanyx
set.filter: (set(a), (a -> bool)) -> set(a)
```

The elements of a set that pass a test, in the same order.

```nanyx
set.fromList([1, 2, 3, 4]) \set.filter { n -> n % 2 == 0 } \set.toList
-- [2, 4]
```

---

## isSubset

```nanyx
set.isSubset: [Eq(a) & Hash(a)] (set(a), set(a)) -> bool
```

Whether the second set holds every element of the first.

```nanyx
set.isSubset(set.fromList([1]), set.fromList([1, 2]))      -- true
set.isSubset(set.fromList([1, 3]), set.fromList([1, 2]))   -- false
```

---

## union

```nanyx
set.union: [Eq(a) & Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set, then those of the second that the first does not hold.

```nanyx
set.union(set.fromList([1, 2, 3]), set.fromList([2, 3, 4])) \set.toList
-- [1, 2, 3, 4]
```

---

## intersection

```nanyx
set.intersection: [Eq(a) & Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set that the second holds too, in the order of the first.

---

## difference

```nanyx
set.difference: [Eq(a) & Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set that the second does not hold, in the order of the first.
