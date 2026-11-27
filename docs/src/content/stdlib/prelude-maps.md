---
title: "prelude: maps and sets"
description: Maps from keys to values, and sets of values each held once
order: 8
---

Functions on `map(k, v)` and `set(a)`, and `list.groupBy`, which gives a map. They are [attached definitions](../advanced/attached-definitions) on `map` and `set`, so `m \insert(k, v)` resolves to `map.insert` when `m` is a map, and are in the [prelude](prelude), so no import is needed. Maps and sets are immutable: every function gives back a new one. How they are built is in [Maps and sets](../deep-dives/maps-and-sets) in the Deep dives section.

# Maps

A `map(k, v)` holds one value of type `v` for each of its keys, of type `k`. It is written with the square-bracket literal syntax and `=>`, and calling a map looks a key up:

```nanyx
def prices = ["tea" => 3, "cake" => 4]

println(prices("tea"))
println(prices("pie"))
-- #some(3)
-- #notFound
```

A literal whose items are all `key => value` is a `map(k, v)`. A key written twice in a literal holds the value written last. `[]` is the empty list unless a map is expected, as in `def none: map(string, int) = []`; `map.empty` is the empty map anywhere. `for prices | name, price -> ...` walks a map's entries.

A map's keys need an [`Eq`](../advanced/contexts#eqa) and a [`Hash`](../advanced/contexts#hasha) of their type, so every function that looks one up requires `[Eq(k), Hash(k)]`. Numbers, strings, tags, records, tuples and lists can all be keys, and so can a nominal type that attaches its own `==` and `hash`; a type that holds a function cannot, and nor can a nominal type that attaches `==` without `hash`. When the keys are tags of different names, write the key type, as in `def lights: map(#red | #green, string) = ...`: inference does not yet join the tags of separate keys. Two maps are `==` when they hold the same keys, each with an equal value, whatever order the keys were added in; the values are compared by their own `Eq`. Maps cannot be ordered with `<`, or be keys themselves, yet.

A map can be printed and interpolated when its keys and values can: it is written as its literal is, its entries in the order of their keys' hashes, each key and value by its own [`ToString`](prelude-contexts#tostring). The empty map is written `[=>]`.

```nanyx fragment
"{["tea" => 3, "cake" => 1]}"   -- "[tea => 3, cake => 1]"
```

# Sets

A `set(a)` holds each of its elements once. A set is written as a literal with its name before the brackets, `set[1, 2, 3]`, or as a plain `[...]` where a set is expected (see [Collections](../tour/collections#syntax)); it is also made from a list with `set.fromList`, or from `set.empty` with `set.insert`. `for s | x -> ...` walks a set's elements.

```nanyx
def seen = set.fromList([3, 1, 3, 2])

println(set.contains(seen, 2))
println(set.length(seen))
-- true
-- 3
```

A set's elements need an `Eq` and a `Hash` of their type, as a map's keys do, so every function that looks one up requires `[Eq(a), Hash(a)]`. Two sets are `==` when they hold the same elements, whatever order they were added in. Sets cannot be ordered with `<`, or be keys or elements of a set, yet. A set can be printed and interpolated when its elements can: it is written as `set.toList` of it would be, `"{set.fromList([3, 1, 3])}"` as `"[3, 1]"`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `map.empty`

```nanyx fragment
def map.empty: map(k, v)
```

The map with no keys. `[]` where a map is expected is this map.

## `map.fromList`

```nanyx fragment
def map.fromList: [Eq(k), Hash(k)] list((k, v)) -> map(k, v)
```

The map holding each of `entries`, in order: of two entries with the same key, the later wins.

## `map.fromArray`

```nanyx fragment
def map.fromArray: [Eq(k), Hash(k)] array((k, v)) -> map(k, v)
```

The map holding each of `entries`, in order: of two entries with the same key, the later wins.
What a literal builds a map with where a map is expected, as `[]` is, or written `map[...]`.

## `map.fromSeq`

```nanyx fragment
def map.fromSeq: [Eq(k), Hash(k), c] ([c] seq((k, v))) -> map(k, v)
```

The map holding each of the entries of `s`, pulled now, in order: of two entries with the same
key, the later wins, as in [`map.fromList`](#mapfromlist). It requires the contexts the sequence's steps do.

## `map.build`

```nanyx fragment
def map.build: [Eq(k), Hash(k), c] ([c, Emit((k, v))] () -> ()) -> map(k, v)
```

The map of the entries `block` emits, through `Emit`: `map.build { emit(("a", 1)) }`. Of two
entries with the same key, the later wins. The same as `list.build(block) \map.fromList`.

## `map.toList`

```nanyx fragment
def map.toList: map(k, v) -> list((k, v))
```

The entries of `m`, each a key and its value, in the order of their keys' hashes: an order that
is the same for two maps with the same keys, however they were built, but that a program should
not rely on. Sort the list for an order of your own: `map.toList(m) \list.sortBy { entry -> entry.0 }`.

## `map.toSeq`

```nanyx fragment
def map.toSeq: map(k, v) -> seq((k, v))
```

The entries of `m` as a sequence of key-value pairs, in the order [`map.toList`](#maptolist) gives them.

## `map.get`

```nanyx fragment
def map.get: [Eq(k), Hash(k)] map(k, v), k -> #some(v) | #notFound
```

The value `m` holds for `key`, or `#notFound`. Calling the map, `m(key)`, is the same.

## `map.containsKey`

```nanyx fragment
def map.containsKey: [Eq(k), Hash(k)] map(k, v), k -> bool
```

Whether `m` holds a value for `key`.

## `map.insert`

```nanyx fragment
def map.insert: [Eq(k), Hash(k)] map(k, v), k, v -> map(k, v)
```

`m` with `key` holding `value`, in place of any value it held before.

## `map.remove`

```nanyx fragment
def map.remove: [Eq(k), Hash(k)] map(k, v), k -> map(k, v)
```

`m` without `key` or its value. A map that has no `key` is given back as it is.

## `map.length`

```nanyx fragment
def map.length: map(k, v) -> int
```

The number of keys.

## `list.groupBy`

```nanyx fragment
def list.groupBy: [Eq(b), Hash(b), c] list(a), ([c] a -> b) -> map(b, list(a))
```

The elements of `xs` grouped by `key`: a map from each key to the elements that have it, in
order.

## `map.keys`

```nanyx fragment
def map.keys: map(k, v) -> list(k)
```

The keys of `m`, in the order of [`map.toList`](#maptolist).

## `map.values`

```nanyx fragment
def map.values: map(k, v) -> list(v)
```

The values of `m`, in the order of [`map.toList`](#maptolist).

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

## `map.foldWhile`

```nanyx fragment
def map.foldWhile: [c] map(k, v), b, ([c] (b, k, v) -> #some(b) | r) -> #some(b) | r
```

[`map.fold`](#mapfold) that can stop part-way: `step` gives `#some(acc)` to go on to the next key and
value, and anything else it gives ends the fold there and is its result, as it is. A fold that
runs to the end gives `#some(acc)`. `step` isn't called again after it stops.

## `map.reduce`

```nanyx fragment
def map.reduce: [c] map(k, v), ([c] (v, v) -> v) -> #some(v) | #none
```

The values of `m` combined with `f`, in the order of [`map.toList`](#maptolist), starting from the first: `["a" => 4, "b" => 5]
\map.reduce { * }` is `#some(20)`, and an empty map, having no first value, gives `#none`. To
start from another value, use [`map.fold`](#mapfold).

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

## `map.filterMap`

```nanyx fragment
def map.filterMap: [c] map(k, v), ([c] (k, v) -> #some(w) | #skip) -> map(k, w)
```

The entries of `m` whose key and value `f` gives `#some` for, each with the new value it gives,
in the same order: a [`map.filter`](#mapfilter) and a [`map.map`](#mapmap) in one walk. Keys `f` gives `#skip` for
are left out.

## `map.any`

```nanyx fragment
def map.any: [c] map(k, v), ([c] (k, v) -> bool) -> bool
```

Whether `test` holds for any key and its value (so `false` for an empty map). It stops at the
first entry it holds for.

## `map.all`

```nanyx fragment
def map.all: [c] map(k, v), ([c] (k, v) -> bool) -> bool
```

Whether `test` holds for every key and its value (so `true` for an empty map). It stops at the
first entry it fails for.

## `map.update`

```nanyx fragment
def map.update: [Eq(k), Hash(k), c] map(k, v), k, ([c] #some(v) | #notFound -> v) -> map(k, v)
```

`m` with `key` holding `f` of what `m` holds for it, `#some(value)` or `#notFound`. A key `m`
does not have is added.

## `map.union`

```nanyx fragment
def map.union: [Eq(k), Hash(k)] map(k, v), map(k, v) -> map(k, v)
```

The entries of `m`, then those of `other` whose keys `m` does not have. Where both have a key,
the value is `other`'s, as a later entry wins in `map.fromList`, so `defaults \map.union(overrides)`
gives the overrides.

## `map.unionWith`

```nanyx fragment
def map.unionWith: [Eq(k), Hash(k), c] map(k, v), map(k, v), ([c] (v, v) -> v) -> map(k, v)
```

As `map.union`, but where both have a key the value is `combine` of `m`'s value and `other`'s.

## `set.empty`

```nanyx fragment
def set.empty: set(a)
```

## `set.fromList`

```nanyx fragment
def set.fromList: [Eq(a), Hash(a)] list(a) -> set(a)
```

The set of the elements of `xs`, each once.

## `set.fromArray`

```nanyx fragment
def set.fromArray: [Eq(a), Hash(a)] array(a) -> set(a)
```

The set of the elements of `xs`, each once. What a literal
builds a set with, `set[1, 2]`, or `[1, 2]` where a set is expected.

## `set.fromSeq`

```nanyx fragment
def set.fromSeq: [Eq(a), Hash(a), c] ([c] seq(a)) -> set(a)
```

The set of the elements of `s`, pulled now, each once. It
requires the contexts the sequence's steps do.

## `set.build`

```nanyx fragment
def set.build: [Eq(a), Hash(a), c] ([c, Emit(a)] () -> ()) -> set(a)
```

The set of what `block` emits, through `Emit`: `set.build { emit(1); emit(1) }` holds one `1`.
The same as `list.build(block) \set.fromList`.

## `set.toList`

```nanyx fragment
def set.toList: set(a) -> list(a)
```

The elements of `s`, in the order of their hashes, as [`map.toList`](#maptolist) gives a map's keys: the same
for two sets with the same elements, however they were built, but not one a program should rely on.

## `set.toSeq`

```nanyx fragment
def set.toSeq: set(a) -> seq(a)
```

The elements of `s` as a sequence, in the order [`set.toList`](#settolist) gives them.

## `set.contains`

```nanyx fragment
def set.contains: [Eq(a), Hash(a)] set(a), a -> bool
```

Whether `s` holds `x`.

## `set.insert`

```nanyx fragment
def set.insert: [Eq(a), Hash(a)] set(a), a -> set(a)
```

`s` with `x` added. A set that holds `x` is given back as it is.

## `set.remove`

```nanyx fragment
def set.remove: [Eq(a), Hash(a)] set(a), a -> set(a)
```

`s` without `x`. A set that does not hold `x` is given back as it is.

## `set.length`

```nanyx fragment
def set.length: set(a) -> int
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

## `set.foldWhile`

```nanyx fragment
def set.foldWhile: [c] set(a), b, ([c] (b, a) -> #some(b) | r) -> #some(b) | r
```

[`set.fold`](#setfold) that can stop part-way: `step` gives `#some(acc)` to go on to the next element,
and anything else it gives ends the fold there and is its result, as it is. A fold that runs to the
end gives `#some(acc)`. `step` isn't called again after it stops.

## `set.reduce`

```nanyx fragment
def set.reduce: [c] set(a), ([c] (a, a) -> a) -> #some(a) | #none
```

The elements of `s` combined with `f`, in the order of [`set.toList`](#settolist), starting from the first: `set[1, 2, 3, 4, 5]
\set.reduce { * }` is `#some(120)`, and an empty set, having no first element, gives `#none`. To
start from another value, use [`set.fold`](#setfold).

## `set.map`

```nanyx fragment
def set.map: [Eq(b), Hash(b), c] set(a), ([c] a -> b) -> set(b)
```

The set of `f` of each element of `s`. Elements that `f` makes equal are kept once, where the
first of them was.

## `set.filter`

```nanyx fragment
def set.filter: [c] set(a), ([c] a -> bool) -> set(a)
```

The elements of `s` that pass `keep`, in the same order.

## `set.filterMap`

```nanyx fragment
def set.filterMap: [Eq(b), Hash(b), c] set(a), ([c] a -> #some(b) | #skip) -> set(b)
```

The set of the values `f` gives as `#some`, leaving out the elements it gives `#skip` for: a
[`set.filter`](#setfilter) and a [`set.map`](#setmap) in one walk. Values that come out equal are kept once, where the
first of them was.

## `set.select`

```nanyx fragment
def set.select: [Eq(b), Hash(b), c] set(a), ([c, Emit(b)] a -> ()) -> set(b)
```

The set of the values `f` emits for each element in turn, through `Emit`, as in
[`list.select`](prelude-lists#listselect). Values that come out equal are kept once, where the first of them was.

## `set.any`

```nanyx fragment
def set.any: [c] set(a), ([c] a -> bool) -> bool
```

Whether `test` holds for any element (so `false` for an empty set). It stops at the first element
it holds for.

## `set.all`

```nanyx fragment
def set.all: [c] set(a), ([c] a -> bool) -> bool
```

Whether `test` holds for every element (so `true` for an empty set). It stops at the first
element it fails for.

## `set.isSubset`

```nanyx fragment
def set.isSubset: [Eq(a), Hash(a)] set(a), set(a) -> bool
```

Whether `t` holds every element of `s`.

## `set.union`

```nanyx fragment
def set.union: [Eq(a), Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s` and those of `t`.

## `set.intersection`

```nanyx fragment
def set.intersection: [Eq(a), Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s` that `t` holds too.

## `set.difference`

```nanyx fragment
def set.difference: [Eq(a), Hash(a)] set(a), set(a) -> set(a)
```

The elements of `s` that `t` does not hold.

<!-- End of the generated reference. -->
