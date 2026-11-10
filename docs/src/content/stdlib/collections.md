---
title: collections
description: Operations on collections such as lists, maps, and sets
order: 3
---

The `collections` module provides functions for working with lists, and other collection types. Maps and sets have pages of their own: [map](map) and [set](set).

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

## contains

```nanyx
List.contains: [Eq(a)] (list(a), a) -> bool
```

Returns `true` if any element is equal to the value. Elements are compared with `==`, by their structure.

```nanyx
[1, 2, 3] \List.contains(2)
-- true
```

---

## count

```nanyx
List.count: (list(a), (a -> bool)) -> int
```

Returns the number of elements that satisfy the predicate.

```nanyx
[1, 2, 3, 4] \List.count { > 2 }
-- 2
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

## dropLast

```nanyx
List.dropLast: list(a) -> list(a)
```

Returns all the elements but the last. An empty list gives an empty list.

```nanyx
List.dropLast([1, 2, 3])
-- [1, 2]
```

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

## flatMap

```nanyx
List.flatMap: (list(a), (a -> list(b))) -> list(b)
```

Applies a function that gives a list to every element, and joins the lists end to end. It is a `map` followed by a `flatten`.

```nanyx
[1, 2, 3] \List.flatMap { x -> [x, x * 10] }
-- [1, 10, 2, 20, 3, 30]
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
List.groupBy: [Eq(b) + Hash(b)] (list(a), (a -> b)) -> map(b, list(a))
```

Groups elements of a list by a key function, returning a [map](map) from each key to the elements that have it, in order. The keys are in the order they first appear. Keys need an `Eq` and a `Hash`, as a map's do.

```nanyx
[1, 2, 3, 4] \List.groupBy { % 2 }
-- [1 => [1, 3], 0 => [2, 4]]
```

---

## indexOf

```nanyx
List.indexOf: [Eq(a)] (list(a), a) -> #some(int) | #notFound
```

Returns the position of the first element equal to the value, counting from 0, or `#notFound`. Elements are compared with `==`, by their structure.

```nanyx
["a", "b", "c"] \List.indexOf("b")
-- #some(1)
```

---

## isEmpty

```nanyx
List.isEmpty: list(a) -> bool
```

Returns `true` if the list has no elements.

```nanyx
List.isEmpty([])
-- true
```

---

## last

```nanyx
List.last: list(a) -> #some(a) | #emptyList
```

Returns the last element if the list is non-empty.

```nanyx
List.last([10, 20, 30])
-- #some(30)
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

## mapIndexed

```nanyx
List.mapIndexed: (list(a), ((int, a) -> b)) -> list(b)
```

Like `map`, but the function is also given each element's position, counting from 0.

```nanyx
["a", "b"] \List.mapIndexed { i, s -> "{i}:{s}" }
-- ["0:a", "1:b"]
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

## takeWhile

```nanyx
List.takeWhile: (list(a), (a -> bool)) -> list(a)
```

Returns the elements before the first one that fails the predicate.

```nanyx
[1, 2, 5, 1] \List.takeWhile { < 3 }
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
