---
title: "prelude: maps and sets"
description: Maps from keys to values, and sets of values each held once
order: 8
---

Functions on `map(k, v)` and `set(a)`, and `list.groupBy`, which gives a map. They are [attached definitions](../advanced/attached-definitions) on `map` and `set`, so `m \insert(k, v)` resolves to `map.insert` when `m` is a map, and are in the [prelude](prelude), so no import is needed. Maps and sets are immutable: every function gives back a new one. For now a map keeps its entries in the order their keys were first added, a set its elements in the order they were first added, and a lookup takes time in proportion to the size (see [Maps and sets](../work-in-progress/maps-and-sets)).

# Maps

A `map(k, v)` holds one value of type `v` for each of its keys, of type `k`. It is written with the square-bracket literal syntax and `=>`, and calling a map looks a key up:

```nanyx
import nanyx/console

def prices = ["tea" => 3, "cake" => 4]

println(prices("tea"))
println(prices("pie"))
-- #some(3)
-- #notFound
```

A literal whose items are all `key => value` is a `map(k, v)`. A key written twice in a literal holds the value written last. `[]` is always the empty list; the empty map is `map.empty`. `for prices | name, price -> ...` walks a map's entries.

A map's keys need an [`Eq`](../advanced/contexts#eqa) and a [`Hash`](../advanced/contexts#hasha) of their type, so every function that looks one up requires `[Eq(k) & Hash(k)]`. Numbers, strings, tags, records, tuples and lists can all be keys, and so can a nominal type that attaches its own `==` and `hash`; a type that holds a function cannot, and nor can a nominal type that attaches `==` without `hash`. When the keys are tags of different names, write the key type, as in `def lights: map(#red | #green, string) = ...`: inference does not yet join the tags of separate keys. Two maps are `==` when they hold the same keys, each with an equal value, whatever order the keys were added in; the values are compared by their own `Eq`. Maps cannot be ordered with `<`, or be keys themselves, yet.

A map can be printed and interpolated when its keys and values can: it is written as its literal is, its entries in the order its keys were first added, each key and value by its own [`ToString`](prelude-contexts#tostring). The empty map is written `[=>]`.

```nanyx fragment
"{["tea" => 3, "cake" => 1]}"   -- "[tea => 3, cake => 1]"
```

# Sets

A `set(a)` holds each of its elements once. There is no set literal yet: a set is made from a list with `set.fromList`, or from `set.empty` with `set.insert`. Literals that build any collection type, through the target type's builder, are planned; see [Collections](../tour/collections#literal-types). `for s | x -> ...` walks a set's elements.

```nanyx
import nanyx/console

def seen = set.fromList([3, 1, 3, 2])

println(set.contains(seen, 2))
println(set.count(seen))
-- true
-- 3
```

A set's elements need an `Eq` and a `Hash` of their type, as a map's keys do, so every function that looks one up requires `[Eq(a) & Hash(a)]`. Two sets are `==` when they hold the same elements, whatever order they were added in. Sets cannot be ordered with `<`, or be keys or elements of a set, yet. A set can be printed and interpolated when its elements can: it is written as `set.toList` of it would be, `"{set.fromList([3, 1, 3])}"` as `"[3, 1]"`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `map.empty`

```nanyx fragment
def map.empty: map(k, v)
```

The map with no keys. A map literal has at least one entry, since [] is the empty list.

## `map.fromList`

```nanyx fragment
def map.fromList: [Eq(k) & Hash(k)] list((k, v)) -> map(k, v)
```

The map holding each of `entries`, in order: of two entries with the same key, the later wins.

## `map.toList`

```nanyx fragment
def map.toList: map(k, v) -> list((k, v))
```

The entries of `m`, each a key and its value, in the order the keys were first added.

## `map.get`

```nanyx fragment
def map.get: [Eq(k) & Hash(k)] map(k, v), k -> #some(v) | #notFound
```

The value `m` holds for `key`, or `#notFound`. Calling the map, `m(key)`, is the same.

## `map.containsKey`

```nanyx fragment
def map.containsKey: [Eq(k) & Hash(k)] map(k, v), k -> bool
```

Whether `m` holds a value for `key`.

## `map.insert`

```nanyx fragment
def map.insert: [Eq(k) & Hash(k)] map(k, v), k, v -> map(k, v)
```

`m` with `key` holding `value`, in place of any value it held before.

## `map.remove`

```nanyx fragment
def map.remove: [Eq(k) & Hash(k)] map(k, v), k -> map(k, v)
```

`m` without `key` or its value. A map that has no `key` is given back as it is.

## `map.count`

```nanyx fragment
def map.count: map(k, v) -> int
```

The number of keys.

## `list.groupBy`

```nanyx fragment
def list.groupBy: [Eq(b) & Hash(b) & c] list(a), ([c] a -> b) -> map(b, list(a))
```

The elements of `xs` grouped by `key`: a map from each key to the elements that have it, in
order. The keys are in the order they first appear.

## `map.keys`

```nanyx fragment
def map.keys: map(k, v) -> list(k)
```

The keys of `m`, in the order they were first added.

## `map.values`

```nanyx fragment
def map.values: map(k, v) -> list(v)
```

The values of `m`, in the order their keys were first added.

## `map.isEmpty`

```nanyx fragment
def map.isEmpty: map(k, v) -> bool
```

Whether `m` has no keys.

## `map.fold`

```nanyx fragment
def map.fold: [c] map(k, v), b, ([c] (b, k, v) -> b) -> b
```

`step` applied to `acc` and each key and value of `m` in turn, in the order of [`map.toList`](#maptolist).

## `map.map`

```nanyx fragment
def map.map: [c] map(k, v), ([c] v -> w) -> map(k, w)
```

`m` with `f` applied to each value. The keys stay as they are, in the same order.

## `map.filter`

```nanyx fragment
def map.filter: [c] map(k, v), ([c] (k, v) -> bool) -> map(k, v)
```

The entries of `m` whose key and value pass `keep`, in the same order.

## `map.update`

```nanyx fragment
def map.update: [Eq(k) & Hash(k) & c] map(k, v), k, ([c] #some(v) | #notFound -> v) -> map(k, v)
```

`m` with `key` holding `f` of what `m` holds for it, `#some(value)` or `#notFound`. A key `m`
does not have is added at the end.

## `map.union`

```nanyx fragment
def map.union: [Eq(k) & Hash(k)] map(k, v), map(k, v) -> map(k, v)
```

The entries of `m`, then those of `other` whose keys `m` does not have. Where both have a key,
the value is `other`'s, as a later entry wins in `map.fromList`, so `defaults \map.union(overrides)`
gives the overrides.

## `map.unionWith`

```nanyx fragment
def map.unionWith: [Eq(k) & Hash(k) & c] map(k, v), map(k, v), ([c] (v, v) -> v) -> map(k, v)
```

As `map.union`, but where both have a key the value is `combine` of `m`'s value and `other`'s.

## `set.empty`

```nanyx fragment
def set.empty: set(a)
```

The set with no elements.

## `set.fromList`

```nanyx fragment
def set.fromList: [Eq(a) & Hash(a)] list(a) -> set(a)
```

The set of the elements of `xs`, each once, in the order they first appear.

## `set.toList`

```nanyx fragment
def set.toList: set(a) -> list(a)
```

The elements of `s`, in the order they were first added.

## `set.contains`

```nanyx fragment
def set.contains: [Eq(a) & Hash(a)] set(a), a -> bool
```

Whether `s` holds `x`.

## `set.insert`

```nanyx fragment
def set.insert: [Eq(a) & Hash(a)] set(a), a -> set(a)
```

`s` with `x` added at the end. A set that holds `x` is given back as it is.

## `set.remove`

```nanyx fragment
def set.remove: [Eq(a) & Hash(a)] set(a), a -> set(a)
```

`s` without `x`. A set that does not hold `x` is given back as it is.

## `set.count`

```nanyx fragment
def set.count: set(a) -> int
```

The number of elements.

## `set.isEmpty`

```nanyx fragment
def set.isEmpty: set(a) -> bool
```

Whether `s` has no elements.

## `set.fold`

```nanyx fragment
def set.fold: [c] set(a), b, ([c] (b, a) -> b) -> b
```

`step` applied to `acc` and each element of `s` in turn, in the order of [`set.toList`](#settolist).

## `set.map`

```nanyx fragment
def set.map: [Eq(b) & Hash(b) & c] set(a), ([c] a -> b) -> set(b)
```

The set of `f` of each element of `s`. Elements that `f` makes equal are kept once, where the
first of them was.

## `set.filter`

```nanyx fragment
def set.filter: [c] set(a), ([c] a -> bool) -> set(a)
```

The elements of `s` that pass `keep`, in the same order.

## `set.isSubset`

```nanyx fragment
def set.isSubset: [Eq(a) & Hash(a)] set(a), set(a) -> bool
```

Whether `t` holds every element of `s`.

## `set.union`

```nanyx fragment
def set.union: [Eq(a) & Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s`, then those of `t` that `s` does not hold.

## `set.intersection`

```nanyx fragment
def set.intersection: [Eq(a) & Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s` that `t` holds too, in the order of `s`.

## `set.difference`

```nanyx fragment
def set.difference: [Eq(a) & Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s` that `t` does not hold, in the order of `s`.

<!-- End of the generated reference. -->
