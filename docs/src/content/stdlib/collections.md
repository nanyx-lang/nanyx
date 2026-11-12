---
title: collections
description: Operations on collections such as lists, maps, and sets
order: 3
---

The `collections` module provides functions for working with lists, and other collection types. Maps and sets have pages of their own: [map](map) and [set](set).

# List

Since lists are immutable, all transform operations on lists return new lists. When nothing else refers to the list you transform, the new list may be built in the old one's memory: `list.map` rewrites its cells in place (see [Reference counting](../foundations/reference-counting#reuse-in-place)).

These functions are [attached definitions](../advanced/attached-definitions) on `list`, so `xs \map(f)` resolves to `list.map` without qualification when `xs` is a list. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed (all but `select` and `selectOne`, which are not there yet).

Every one of them is stack-safe: `map`, `filter`, `take`, `flatten`, `zip`, `sortBy`, `unfold` and the rest work on a list of any length without running out of stack. Each walks the list with [tail calls](../tour/functions#tail-recursion), and those that build a list build it in reverse and reverse it at the end.

## all

```nanyx
list.all: (list(a), (a -> bool)) -> bool
```

Returns `true` if all elements satisfy the predicate.

```nanyx
[1, 2, 3] \list.all { > 0 }
-- true
```

Note: it can seem counter intuitive, but `list.all` always returns `true` for an empty list, since there are no elements that fail the predicate.

---

## any

```nanyx
list.any: (list(a), (a -> bool)) -> bool
```

Returns `true` if any element satisfies the predicate.

```nanyx
[1, 2, 3] \list.any { > 2 }
-- true
```

Note: it can seem counter intuitive, but `list.any` returns `false` for an empty list, since there are no elements that satisfy the predicate.

---

## append

```nanyx
list.append: (list(a), a) -> list(a)
```

Returns a new list with the element added to the end.

```nanyx
[1, 2] \list.append(3)
-- [1, 2, 3]
```

---

## contains

```nanyx
list.contains: [Eq(a)] (list(a), a) -> bool
```

Returns `true` if any element is equal to the value. Elements are compared with `==`, by their structure.

```nanyx
[1, 2, 3] \list.contains(2)
-- true
```

---

## count

```nanyx
list.count: (list(a), (a -> bool)) -> int
```

Returns the number of elements that satisfy the predicate.

```nanyx
[1, 2, 3, 4] \list.count { > 2 }
-- 2
```

---

## distinct

```nanyx
list.distinct: [Eq(a)] list(a) -> list(a)
```

Returns a new list with duplicate values removed, keeping first occurrence order.

```nanyx
list.distinct([1, 2, 2, 3, 1])
-- [1, 2, 3]
```

Elements are compared with `==`, which compares records, tuples, lists and tags by their structure. A list of functions cannot be made distinct, since functions have no equality: it is a type error. See [`Eq(a)`](../advanced/contexts#eqa).

---

## dropLast

```nanyx
list.dropLast: list(a) -> list(a)
```

Returns all the elements but the last. An empty list gives an empty list.

```nanyx
list.dropLast([1, 2, 3])
-- [1, 2]
```

---

## filter

```nanyx
list.filter: (list(a), (a -> bool)) -> list(a)
```

Keeps only elements that satisfy the predicate.

```nanyx
[1, 2, 3, 4, 5] \list.filter { > 3 }
-- [4, 5]
```

---

## find

```nanyx
list.find: (list(a), (a -> bool)) -> #some(a) | #notFound
```

Returns the first element matching the predicate.

```nanyx
[1, 2, 3] \list.find { > 1 }
-- #some(2)
```

---

## flatMap

```nanyx
list.flatMap: (list(a), (a -> list(b))) -> list(b)
```

Applies a function that gives a list to every element, and joins the lists end to end. It is a `map` followed by a `flatten`.

```nanyx
[1, 2, 3] \list.flatMap { x -> [x, x * 10] }
-- [1, 10, 2, 20, 3, 30]
```

---

## flatten

```nanyx
list.flatten: list(list(a)) -> list(a)
```

Flattens a list of lists into a single list.

```nanyx
list.flatten([[1, 2], [3, 4]])
-- [1, 2, 3, 4]
```

---

## head

```nanyx
list.head: list(a) -> #some(a) | #emptyList
```

Returns the first element if the list is non-empty.

```nanyx
list.head([10, 20, 30])
-- #some(10)
```

---

## fold

```nanyx
list.fold: (list(a), b, ((b, a) -> b)) -> b
```

Reduces a list to a single value. Runs through the list from left to right, applying the function to an accumulator and each element.

```nanyx
[1, 2, 3] \list.fold(0) { + }
-- 6
```

---

## foldBack

```nanyx
list.foldBack: (list(a), b, ((b, a) -> b)) -> b
```

Reduces a list to a single value. Runs through the list backwards (from right to left), applying the function to an accumulator and each element.

```nanyx
[1, 2, 3] \list.foldBack(0) { - }
-- -6, i.e. ((0 - 3) - 2) - 1
```

---

## get

```nanyx
list.get: (list(a), int) -> #some(a) | #outOfRange
```

Returns the element at an index, counting from 0. A negative index, or one past the end, gives `#outOfRange`.

```nanyx
[10, 20, 30] \list.get(1)
-- #some(20)

[10, 20, 30] \list.get(3)
-- #outOfRange
```

---
## groupBy

```nanyx
list.groupBy: [Eq(b) & Hash(b)] (list(a), (a -> b)) -> map(b, list(a))
```

Groups elements of a list by a key function, returning a [map](map) from each key to the elements that have it, in order. The keys are in the order they first appear. Keys need an `Eq` and a `Hash`, as a map's do.

```nanyx
[1, 2, 3, 4] \list.groupBy { % 2 }
-- [1 => [1, 3], 0 => [2, 4]]
```

---

## indexOf

```nanyx
list.indexOf: [Eq(a)] (list(a), a) -> #some(int) | #notFound
```

Returns the position of the first element equal to the value, counting from 0, or `#notFound`. Elements are compared with `==`, by their structure.

```nanyx
["a", "b", "c"] \list.indexOf("b")
-- #some(1)
```

---

## isEmpty

```nanyx
list.isEmpty: list(a) -> bool
```

Returns `true` if the list has no elements.

```nanyx
list.isEmpty([])
-- true
```

---

## last

```nanyx
list.last: list(a) -> #some(a) | #emptyList
```

Returns the last element if the list is non-empty.

```nanyx
list.last([10, 20, 30])
-- #some(30)
```

---

## length

```nanyx
list.length: list(a) -> int
```

Returns the number of elements.

```nanyx
list.length([1, 2, 3])
-- 3
```

---

## map

```nanyx
list.map: (list(a), (a -> b)) -> list(b)
```

Applies a function to every element in a list, collecting the results in a new list. The resulting list always has the same length as the input list.

```nanyx
[1, 2, 3] \list.map { * 2 }
-- [2, 4, 6]
```

---

## mapIndexed

```nanyx
list.mapIndexed: (list(a), ((int, a) -> b)) -> list(b)
```

Like `map`, but the function is also given each element's position, counting from 0.

```nanyx
["a", "b"] \list.mapIndexed { i, s -> "{i}:{s}" }
-- ["0:a", "1:b"]
```

---

## max

```nanyx
list.max: [Ord(a)] list(a) -> #some(a) | #emptyList
```

Returns the greatest element, or `#emptyList` for an empty list. Of several greatest elements, the first. Elements are ordered by their [`Ord`](../advanced/contexts#orda), so a list of records has no `max`: use a key with `sortBy`.

```nanyx
list.max([3, 8, 1])
-- #some(8)
```

---

## min

```nanyx
list.min: [Ord(a)] list(a) -> #some(a) | #emptyList
```

Returns the least element, or `#emptyList` for an empty list. Of several least elements, the first.

```nanyx
list.min(["pear", "apple", "fig"])
-- #some("apple")
```

---

## partition

```nanyx
list.partition: (list(a), (a -> bool)) -> (list(a), list(a))
```

Returns the elements that pass the predicate and the elements that fail it, each in their original order.

```nanyx
[1, 2, 3, 4] \list.partition { < 3 }
-- ([1, 2], [3, 4])
```

---
## reverse

```nanyx
list.reverse: list(a) -> list(a)
```

Returns a new list with elements in reverse order.

```nanyx
list.reverse([1, 2, 3])
-- [3, 2, 1]
```

---

## skip

```nanyx
list.skip: (list(a), int) -> list(a)
```

Returns a list without the first `count` elements.

```nanyx
list.skip([1, 2, 3, 4], 2)
-- [3, 4]
```

---

## skipWhile

```nanyx
list.skipWhile: (list(a), (a -> bool)) -> list(a)
```

Returns the elements from the first one that fails the predicate, so `xs \takeWhile(f) ++ xs \skipWhile(f)` is `xs`.

```nanyx
[1, 2, 5, 1] \list.skipWhile { < 3 }
-- [5, 1]
```

---
## sort

```nanyx
list.sort: [Ord(a)] list(a) -> list(a)
```

Sorts a list in ascending order of its elements' [`Ord`](../advanced/contexts#orda): numbers numerically, strings by their bytes, tuples and lists position by position. The sort is stable. Records and tags have no order, so sort a list of them with `sortBy`.

```nanyx
list.sort([3, 1, 2])
-- [1, 2, 3]
```

---

## sortBy

```nanyx
list.sortBy: [Ord(b)] (list(a), (a -> b)) -> list(a)
```

Sorts a list in ascending order of the key the projection function gives each element. The key can be of any type with an order, a tuple of keys included. The sort is stable: elements with equal keys keep their order.

```nanyx
list.sortBy(["bbb", "a", "cc"], { .length })
-- ["a", "cc", "bbb"]
```

---

## sum

```nanyx
list.sum: [Add(a)] list(a) -> a
```

Adds the elements together with `+`. The sum of an empty list is `zero`. It works on any type with an [`Add`](../advanced/contexts#adda): ints, floats, and a type that attaches `zero`, `+`, `-` and `negate`.

```nanyx
[1, 2, 3] \list.sum
-- 6

[1.5, 2.5] \list.sum
-- 4.0
```

---

## take

```nanyx
list.take: (list(a), int) -> list(a)
```

Returns the first `count` elements.

```nanyx
list.take([1, 2, 3, 4], 2)
-- [1, 2]
```

---

## takeWhile

```nanyx
list.takeWhile: (list(a), (a -> bool)) -> list(a)
```

Returns the elements before the first one that fails the predicate.

```nanyx
[1, 2, 5, 1] \list.takeWhile { < 3 }
-- [1, 2]
```

---

## select

```nanyx
list.select: (list(a), [Yield(b)] (a -> ())) -> list(b)
```

For every element in the input list, runs the function with a `Yield` context that allows it to yield any number of output elements. Collects all yielded elements into a new list.

`select` can be thought of as a `map` and a `filter` at the same time, or a `map` followed by a `flatten` (but more efficient since it doesn't create intermediate lists).

```nanyx
type Color = #red | #green | #blue | #other(string)

def f: list(Color) -> list(string) = { items ->
    items \list.select { item ->
        if item is #other(v) then yield(v)
    }
}
```

Not in the prelude yet: it waits on the `Yield` context ([#478](https://github.com/nanyx-lang/nanyx/issues/478)).

---

## selectOne

```nanyx
list.selectOne: (list(a), [Yield(b)] (a -> ())) -> #some(b) | #emptyList
```

For every element in the input list, runs the function with a `Yield` context that allows it to yield any number of output elements. Immediately returns the first yielded element wrapped in `#some`, or `#emptyList` if no elements were yielded.

`selectOne` can be thought of as a `map` and a `find` at the same time.

```nanyx
type Color = #red | #green | #blue | #other(string)

def f: list(Color) -> #some(string) | #emptyList = { items ->
    items \list.selectOne { item ->
        if item is #other(v) then yield(v)
    }
}
```

Not in the prelude yet: it waits on the `Yield` context ([#478](https://github.com/nanyx-lang/nanyx/issues/478)).

---

## unfold

```nanyx
list.unfold: (b, (b -> #some(a, b) | #done)) -> list(a)
```

Generates a sequence from an initial state value and a function that produces either `#done` to end the sequence or `#some(element, newState)` to yield an element and a new state for the next step. Returns a list of all yielded elements.

```nanyx
def fibonacci = list.unfold((0, 1)) { current, next ->
    if current > 100 then #done
    else #some(current, (next, current + next))
}
```

---

## unzip

```nanyx
list.unzip: list((a, b)) -> (list(a), list(b))
```

Splits a list of pairs into the list of their first parts and the list of their second parts: the inverse of `zip` for lists of equal length.

```nanyx
list.unzip([(1, "a"), (2, "b")])
-- ([1, 2], ["a", "b"])
```

---
## zip

```nanyx
list.zip: (list(a), list(b)) -> list((a, b))
```

Combines two lists element-by-element into tuples. Length is the smaller input length.

```nanyx
list.zip([1, 2, 3], ["a", "b"])
-- [(1, "a"), (2, "b")]
```
