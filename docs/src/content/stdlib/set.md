---
title: set
description: Sets of values, each held once
order: 5
---

A `set(a)` holds each of its elements once. There is no set literal yet: a set is made from a list with `Set.fromList`, or from `Set.empty` with `Set.insert`. `for s | x -> ...` walks a set's elements. `Set(a)` is another way to write the type.

```nanyx
def seen = Set.fromList([3, 1, 3, 2])

Set.contains(seen, 2)   -- true
Set.count(seen)         -- 3
```

A set's elements need an [`Eq`](../advanced/contexts#eqa) and a [`Hash`](../advanced/contexts#hasha) of their type, as a [map](map)'s keys do, so every function that looks one up requires `[Eq(a) + Hash(a)]`. Two sets cannot be compared with `==` yet ([#618](https://github.com/nanyx-lang/nanyx/issues/618)).

These functions are [attached definitions](../advanced/attached-definitions) on `Set`, so `s \insert(x)` resolves to `Set.insert` when `s` is a set, and are in the [prelude](../tour/modules#modules-in-the-standard-library). Sets are immutable: every function gives back a new set. For now a set keeps its elements in the order they were first added, and `contains` takes time in proportion to its size (see [Maps and sets](../work-in-progress/maps-and-sets)).

## empty

```nanyx
Set.empty: set(a)
```

The set with no elements.

---

## fromList

```nanyx
Set.fromList: [Eq(a) + Hash(a)] list(a) -> set(a)
```

The set of the elements of a list, each once, in the order they first appear.

---

## toList

```nanyx
Set.toList: set(a) -> list(a)
```

The elements of a set, in the order they were first added.

```nanyx
Set.fromList([3, 1, 3, 2]) \Set.toList
-- [3, 1, 2]
```

---

## contains

```nanyx
Set.contains: [Eq(a) + Hash(a)] (set(a), a) -> bool
```

Whether a set holds an element.

---

## insert

```nanyx
Set.insert: [Eq(a) + Hash(a)] (set(a), a) -> set(a)
```

The set with an element added at the end. A set that holds the element already is given back as it is.

---

## remove

```nanyx
Set.remove: [Eq(a) + Hash(a)] (set(a), a) -> set(a)
```

The set without an element. A set that does not hold it is given back as it is.

---

## count

```nanyx
Set.count: set(a) -> int
```

The number of elements in a set.

---

## union

```nanyx
Set.union: [Eq(a) + Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set, then those of the second that the first does not hold.

```nanyx
Set.union(Set.fromList([1, 2, 3]), Set.fromList([2, 3, 4])) \Set.toList
-- [1, 2, 3, 4]
```

---

## intersection

```nanyx
Set.intersection: [Eq(a) + Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set that the second holds too, in the order of the first.

---

## difference

```nanyx
Set.difference: [Eq(a) + Hash(a)] (set(a), set(a)) -> set(a)
```

The elements of the first set that the second does not hold, in the order of the first.
