---
title: collections
description: Operations on collections such as lists, maps, and sets
order: 3
---

The `collections` module provides functions for working with lists, maps, sets, and other collection types.

# List

Since lists are immutable, all transform operations on lists return new lists. When nothing else refers to the list you transform, the new list may be built in the old one's memory: `List.map` rewrites its cells in place (see [Reference counting](../foundations/reference-counting#reuse-in-place)).

These functions are [attached definitions](../advanced/attached-definitions) on `List`, so `xs \map(f)` resolves to `List.map` without qualification when `xs` is a list. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed (all but `select` and `selectOne`, which are not there yet).

Every one of them is stack-safe: `map`, `filter`, `take`, `flatten`, `zip`, `sortBy`, `unfold` and the rest work on a list of any length without running out of stack. Each walks the list with [tail calls](../tour/functions#tail-recursion), and those that build a list build it in reverse and reverse it at the end.

## all

```nanyx
List.all: (list(a), (a -> bool)) -> bool
```

Returns `true` if all elements satisfy the predicate.

```nanyx
[1, 2, 3] \List.all { > 0 }
-- true
```

Note: it can seem counter intuitive, but `List.all` always returns `true` for an empty list, since there are no elements that fail the predicate.

---

## any

```nanyx
List.any: (list(a), (a -> bool)) -> bool
```

Returns `true` if any element satisfies the predicate.

```nanyx
[1, 2, 3] \List.any { > 2 }
-- true
```

Note: it can seem counter intuitive, but `List.any` returns `false` for an empty list, since there are no elements that satisfy the predicate.

---

## append

```nanyx
List.append: (list(a), a) -> list(a)
```

Returns a new list with the element added to the end.

```nanyx
[1, 2] \List.append(3)
-- [1, 2, 3]
```

---

## distinct

```nanyx
List.distinct: [Eq(a)] list(a) -> list(a)
```

Returns a new list with duplicate values removed, keeping first occurrence order.

```nanyx
List.distinct([1, 2, 2, 3, 1])
-- [1, 2, 3]
```

Elements are compared with `==`, which compares records, tuples, lists and tags by their structure. A list of functions cannot be made distinct, since functions have no equality: it is a type error. See [`Eq(a)`](../advanced/contexts#eqa).

---

## filter

```nanyx
List.filter: (list(a), (a -> bool)) -> list(a)
```

Keeps only elements that satisfy the predicate.

```nanyx
[1, 2, 3, 4, 5] \List.filter { > 3 }
-- [4, 5]
```

---

## find

```nanyx
List.find: (list(a), (a -> bool)) -> #some(a) | #notFound
```

Returns the first element matching the predicate.

```nanyx
[1, 2, 3] \List.find { > 1 }
-- #some(2)
```

---

## flatten

```nanyx
List.flatten: list(list(a)) -> list(a)
```

Flattens a list of lists into a single list.

```nanyx
List.flatten([[1, 2], [3, 4]])
-- [1, 2, 3, 4]
```

---

## head

```nanyx
List.head: list(a) -> #some(a) | #emptyList
```

Returns the first element if the list is non-empty.

```nanyx
List.head([10, 20, 30])
-- #some(10)
```

---

## fold

```nanyx
List.fold: (list(a), b, ((b, a) -> b)) -> b
```

Reduces a list to a single value. Runs through the list from left to right, applying the function to an accumulator and each element.

```nanyx
[1, 2, 3] \List.fold(0) { + }
-- 6
```

---

## foldBack

```nanyx
List.foldBack: (list(a), b, ((b, a) -> b)) -> b
```

Reduces a list to a single value. Runs through the list backwards (from right to left), applying the function to an accumulator and each element.

```nanyx
[1, 2, 3] \List.foldBack(0) { - }
-- -6, i.e. ((0 - 3) - 2) - 1
```

---

## groupBy

```nanyx
List.groupBy: [Eq(b)] (list(a), (a -> b)) -> map(b, list(a))
```

Groups elements of a list by a key function, returning a [map](#map) from each key to the elements that have it, in order. The keys are in the order they first appear. Keys are compared with `==`.

```nanyx
[1, 2, 3, 4] \List.groupBy { % 2 }
-- [1 => [1, 3], 0 => [2, 4]]
```

---

## length

```nanyx
List.length: list(a) -> int
```

Returns the number of elements.

```nanyx
List.length([1, 2, 3])
-- 3
```

---

## map

```nanyx
List.map: (list(a), (a -> b)) -> list(b)
```

Applies a function to every element in a list, collecting the results in a new list. The resulting list always has the same length as the input list.

```nanyx
[1, 2, 3] \List.map { * 2 }
-- [2, 4, 6]
```

---

## max

```nanyx
List.max: [Ord(a)] list(a) -> #some(a) | #emptyList
```

Returns the greatest element, or `#emptyList` for an empty list. Of several greatest elements, the first. Elements are ordered by their [`Ord`](../advanced/contexts#orda), so a list of records has no `max`: use a key with `sortBy`.

```nanyx
List.max([3, 8, 1])
-- #some(8)
```

---

## min

```nanyx
List.min: [Ord(a)] list(a) -> #some(a) | #emptyList
```

Returns the least element, or `#emptyList` for an empty list. Of several least elements, the first.

```nanyx
List.min(["pear", "apple", "fig"])
-- #some("apple")
```

---

## reverse

```nanyx
List.reverse: list(a) -> list(a)
```

Returns a new list with elements in reverse order.

```nanyx
List.reverse([1, 2, 3])
-- [3, 2, 1]
```

---

## skip

```nanyx
List.skip: (list(a), int) -> list(a)
```

Returns a list without the first `count` elements.

```nanyx
List.skip([1, 2, 3, 4], 2)
-- [3, 4]
```

---

## sort

```nanyx
List.sort: [Ord(a)] list(a) -> list(a)
```

Sorts a list in ascending order of its elements' [`Ord`](../advanced/contexts#orda): numbers numerically, strings by their bytes, tuples and lists position by position. The sort is stable. Records and tags have no order, so sort a list of them with `sortBy`.

```nanyx
List.sort([3, 1, 2])
-- [1, 2, 3]
```

---

## sortBy

```nanyx
List.sortBy: [Ord(b)] (list(a), (a -> b)) -> list(a)
```

Sorts a list in ascending order of the key the projection function gives each element. The key can be of any type with an order, a tuple of keys included. The sort is stable: elements with equal keys keep their order.

```nanyx
List.sortBy(["bbb", "a", "cc"], { .length })
-- ["a", "cc", "bbb"]
```

---

## sum

```nanyx
List.sum: [Add(a)] list(a) -> a
```

Adds the elements together with `+`. The sum of an empty list is `zero`. It works on any type with an [`Add`](../advanced/contexts#adda): ints, floats, and a type that attaches `zero`, `+`, `-` and `negate`.

```nanyx
[1, 2, 3] \List.sum
-- 6

[1.5, 2.5] \List.sum
-- 4.0
```

---

## take

```nanyx
List.take: (list(a), int) -> list(a)
```

Returns the first `count` elements.

```nanyx
List.take([1, 2, 3, 4], 2)
-- [1, 2]
```

---

## select

```nanyx
List.select: (list(a), [Yield(b)] (a -> ())) -> list(b)
```

For every element in the input list, runs the function with a `Yield` context that allows it to yield any number of output elements. Collects all yielded elements into a new list.

`select` can be thought of as a `map` and a `filter` at the same time, or a `map` followed by a `flatten` (but more efficient since it doesn't create intermediate lists).

```nanyx
type Color = #red | #green | #blue | #other(string)

def f: list(Color) -> list(string) = { items ->
    items \List.select { item ->
        if item is #other(v) then yield(v)
    }
}
```

Not in the prelude yet: it waits on the `Yield` context ([#478](https://github.com/nanyx-lang/nanyx/issues/478)).

---

## selectOne

```nanyx
List.selectOne: (list(a), [Yield(b)] (a -> ())) -> #some(b) | #emptyList
```

For every element in the input list, runs the function with a `Yield` context that allows it to yield any number of output elements. Immediately returns the first yielded element wrapped in `#some`, or `#emptyList` if no elements were yielded.

`selectOne` can be thought of as a `map` and a `find` at the same time.

```nanyx
type Color = #red | #green | #blue | #other(string)

def f: list(Color) -> #some(string) | #emptyList = { items ->
    items \List.selectOne { item ->
        if item is #other(v) then yield(v)
    }
}
```

Not in the prelude yet: it waits on the `Yield` context ([#478](https://github.com/nanyx-lang/nanyx/issues/478)).

---

## unfold

```nanyx
List.unfold: (b, (b -> #some(a, b) | #done)) -> list(a)
```

Generates a sequence from an initial state value and a function that produces either `#done` to end the sequence or `#some(element, newState)` to yield an element and a new state for the next step. Returns a list of all yielded elements.

```nanyx
def fibonacci = List.unfold((0, 1)) { current, next ->
    if current > 100 then #done
    else #some(current, (next, current + next))
}
```

---

## zip

```nanyx
List.zip: (list(a), list(b)) -> list((a, b))
```

Combines two lists element-by-element into tuples. Length is the smaller input length.

```nanyx
List.zip([1, 2, 3], ["a", "b"])
-- [(1, "a"), (2, "b")]
```

# Map

A `map(k, v)` holds one value of type `v` for each of its keys, of type `k`. It is written with the square-bracket literal syntax and `=>`, and calling a map looks a key up:

```nanyx
def prices = ["tea" => 3, "cake" => 4]

prices("tea")   -- #some(3)
prices("pie")   -- #notFound
```

A key written twice in a literal holds the value written last. `[]` is always the empty list; the empty map is `Map.empty`. `for prices | name, price -> ...` walks a map's entries.

Keys are compared with `==`, so every function that looks one up requires an [`Eq`](../advanced/contexts#eqa) of the key type: numbers, strings, tags, records and tuples can all be keys, but a type that holds a function cannot. Two maps cannot be compared with `==` yet ([#618](https://github.com/nanyx-lang/nanyx/issues/618)).

These functions are attached to `Map`, so `m \insert(k, v)` resolves to `Map.insert` when `m` is a map, and are in the [prelude](../tour/modules#modules-in-the-standard-library). Maps are immutable: every function gives back a new map. For now a map keeps its entries in the order their keys were first added, and a lookup takes time in proportion to its size (see [Maps and sets](../work-in-progress/maps-and-sets)).

## empty

```nanyx
Map.empty: map(k, v)
```

The map with no keys.

---

## fromList

```nanyx
Map.fromList: [Eq(k)] list((k, v)) -> map(k, v)
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

## get

```nanyx
Map.get: [Eq(k)] (map(k, v), k) -> #some(v) | #notFound
```

The value a map holds for a key, or `#notFound`. Calling the map, `m(key)`, is the same.

```nanyx
["a" => 1] \Map.get("a")
-- #some(1)
```

---

## containsKey

```nanyx
Map.containsKey: [Eq(k)] (map(k, v), k) -> bool
```

Whether a map holds a value for a key.

---

## insert

```nanyx
Map.insert: [Eq(k)] (map(k, v), k, v) -> map(k, v)
```

The map with a key holding a value, in place of any value it held before.

```nanyx
["a" => 1] \insert("b", 2) \insert("a", 10)
-- ["a" => 10, "b" => 2]
```

---

## remove

```nanyx
Map.remove: [Eq(k)] (map(k, v), k) -> map(k, v)
```

The map without a key or its value. A map that does not have the key is given back as it is.

---

## count

```nanyx
Map.count: map(k, v) -> int
```

The number of keys in a map.
