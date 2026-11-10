---
title: map
description: Maps from keys to values
order: 4
---

A `map(k, v)` holds one value of type `v` for each of its keys, of type `k`. It is written with the square-bracket literal syntax and `=>`, and calling a map looks a key up:

```nanyx
def prices = ["tea" => 3, "cake" => 4]

prices("tea")   -- #some(3)
prices("pie")   -- #notFound
```

A key written twice in a literal holds the value written last. `[]` is always the empty list; the empty map is `Map.empty`. `for prices | name, price -> ...` walks a map's entries. `Map(k, v)` is another way to write the type.

A map's keys need an [`Eq`](../advanced/contexts#eqa) and a [`Hash`](../advanced/contexts#hasha) of their type, so every function that looks one up requires `[Eq(k) + Hash(k)]`. Numbers, strings, tags, records, tuples and lists can all be keys, and so can a nominal type that attaches its own `==` and `hash`; a type that holds a function cannot, and nor can a nominal type that attaches `==` without `hash`. When the keys are tags of different names, write the key type, as in `def lights: map(#red | #green, string) = ...`: inference does not yet join the tags of separate keys. Two maps cannot be compared with `==` yet ([#618](https://github.com/nanyx-lang/nanyx/issues/618)).

These functions are [attached definitions](../advanced/attached-definitions) on `Map`, so `m \insert(k, v)` resolves to `Map.insert` when `m` is a map, and are in the [prelude](../tour/modules#modules-in-the-standard-library). Maps are immutable: every function gives back a new map. For now a map keeps its entries in the order their keys were first added, and a lookup takes time in proportion to its size (see [Maps and sets](../work-in-progress/maps-and-sets)).

## empty

```nanyx
Map.empty: map(k, v)
```

The map with no keys.

---

## fromList

```nanyx
Map.fromList: [Eq(k) + Hash(k)] list((k, v)) -> map(k, v)
```

The map holding each key and value of a list of pairs. Of two pairs with the same key, the later wins.

```nanyx
Map.fromList([(1, "one"), (2, "two"), (1, "uno")])
-- [1 => "uno", 2 => "two"]
```

---

## toList

```nanyx
Map.toList: map(k, v) -> list((k, v))
```

The entries of a map, each a key and its value, in the order the keys were first added.

---

## keys

```nanyx
Map.keys: map(k, v) -> list(k)
```

The keys of a map, in the order they were first added.

---

## values

```nanyx
Map.values: map(k, v) -> list(v)
```

The values of a map, in the order their keys were first added.

---

## get

```nanyx
Map.get: [Eq(k) + Hash(k)] (map(k, v), k) -> #some(v) | #notFound
```

The value a map holds for a key, or `#notFound`. Calling the map, `m(key)`, is the same.

```nanyx
["a" => 1] \Map.get("a")
-- #some(1)
```

---

## containsKey

```nanyx
Map.containsKey: [Eq(k) + Hash(k)] (map(k, v), k) -> bool
```

Whether a map holds a value for a key.

---

## insert

```nanyx
Map.insert: [Eq(k) + Hash(k)] (map(k, v), k, v) -> map(k, v)
```

The map with a key holding a value, in place of any value it held before.

```nanyx
["a" => 1] \insert("b", 2) \insert("a", 10)
-- ["a" => 10, "b" => 2]
```

---

## remove

```nanyx
Map.remove: [Eq(k) + Hash(k)] (map(k, v), k) -> map(k, v)
```

The map without a key or its value. A map that does not have the key is given back as it is.

---

## count

```nanyx
Map.count: map(k, v) -> int
```

The number of keys in a map.
