---
title: collections
description: Operations on collections such as lists, maps, and sets
order: 3
---

The `collections` module provides functions for working with lists, maps, sets, and other collection types.

# List

Since lists are immutable, all transform operations on lists return new lists. When nothing else refers to the list you transform, the new list may be built in the old one's memory: `List.map` rewrites its cells in place (see [Reference counting](../advanced/reference-counting#reuse-in-place)).

These functions are [attached definitions](../advanced/attached-definitions) on `List`, so `xs \map(f)` resolves to `List.map` without qualification when `xs` is a list. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed (all but `groupBy`, `select` and `selectOne`, which are not there yet).

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
List.distinct: list(a) -> list(a)
```

Returns a new list with duplicate values removed, keeping first occurrence order.

```nanyx
List.distinct([1, 2, 2, 3, 1])
-- [1, 2, 3]
```

Elements are compared with `==`, which compares records, tuples, lists and tags by their structure. A list of functions cannot be made distinct, since functions have no equality.

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
List.groupBy: (list(a), (a -> b)) -> map(b, list(a))
```

Groups elements of a list by a key function, returning a map from keys to lists of elements.

```nanyx
[1, 2, 3, 4] \List.groupBy { % 2 }
-- Map [ 0 => [2, 4], 1 => [1, 3] ]
```

Not in the prelude yet: it waits on the `map` type ([#477](https://github.com/nanyx-lang/nanyx/issues/477)).

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

## sortBy

```nanyx
List.sortBy: (list(a), (a -> b)) -> list(a)
```

Sorts a list in ascending order of the key the projection function gives each element. The sort is stable: elements with equal keys keep their order.

```nanyx
List.sortBy(["bbb", "a", "cc"], { .length })
-- ["a", "cc", "bbb"]
```

For now the prelude's `List.sortBy` takes `(a -> int)`: the key must be an `int`, since `<` does not order other types yet ([#476](https://github.com/nanyx-lang/nanyx/issues/476)).

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
