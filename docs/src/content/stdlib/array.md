---
title: array
description: Immutable arrays with constant-time indexing
order: 10.5
---

Its functions are attached to `array`, so they need no import. Reach for an array over a [list](prelude-lists) when you read elements by index: `array.at` takes the same time wherever the element is, where `list.at` walks to it. An array can't grow or change; make a new one instead.

```nanyx
import nanyx/array

def xs = array.fromList([10, 20, 30])

println(array.length(xs))
println(array.at(xs, 1))
println(array.at(xs, 5))
println(xs(2))
println(xs \map({ x -> x // 10 }))
-- 3
-- #some(20)
-- #notFound
-- #some(30)
-- [1, 2, 3]
```

An array is callable: `xs(i)` is `array.at(xs, i)`, so it gives `#some(a) | #notFound` and never traps. Use it as `xs(0) ?? fallback`. `array.at` is in the prelude, so a call of an array works in a module that doesn't import `nanyx/array`.

`array.create(n)` fills each slot with the [`Default`](prelude-contexts#default) of the element type, so it needs to know that type: `def zeros: Array(int) = array.create(3)`. `==` compares two arrays element by element. An array has no order and no hash, so it can't be sorted against another, or put in a set or used as a map's key.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Arrays: immutable, fixed-length sequences with constant-time indexing.

An `Array(a)` holds its elements in one block, so [`array.at`](prelude-arrays#arrayat) reads any of them in the same time,
where `at` on a list walks to it. Nothing about an array changes once it is made, so it needs no
`Memory`. Write one as a literal, `Array[1, 2, 3]`, or make it from a list with [`array.fromList`](#arrayfromlist),
from its indices with [`array.init`](#arrayinit), or filled with each slot's default with [`array.create`](#arraycreate).

Each function is attached to `array`, so it is in scope wherever arrays are, without an import, and is called as
`array.length(xs)`, or as `xs \length` when `xs` is known to be an array. `==` compares two arrays
element by element, and an array is written as a list is, `[10, 20, 30]`.

The functions every ordered collection has are here with `list`'s names, argument order and
meaning: [`array.filter`](#arrayfilter), [`array.find`](#arrayfind), [`array.sort`](#arraysort), [`array.zip`](#arrayzip) and the rest. Each that
makes a new collection gives an array, read and written by index, with no list made on the way.

## `array.fromList`

```nanyx fragment
def array.fromList: list(a) -> array(a)
```

An array of the elements of `xs`, in order.

## `array.fromSeq`

```nanyx fragment
def array.fromSeq: [c] ([c] seq(a)) -> array(a)
```

An array of the elements of `s`, pulled now, in order: `(1..5) \Array.fromSeq` is
`[1, 2, 3, 4, 5]`. It requires the contexts the sequence's steps do. An endless sequence never
ends: cut it with `seq.take`.

## `array.build`

```nanyx fragment
def array.build: [c] ([c, Emit(a)] () -> ()) -> array(a)
```

An array of what `block` emits, through `Emit`: `Array.build { emit(1); if more then emit(2) }`.
The same as `list.build(block) \array.fromList`.

## `array.toList`

```nanyx fragment
def array.toList: array(a) -> list(a)
```

The elements of `xs`, in order, as a list.

## `array.length`

```nanyx fragment
def array.length: array(a) -> int
```

How many elements `xs` holds.

## `array.init`

```nanyx fragment
def array.init: [c] int, ([c] int -> a) -> array(a)
```

An array of `count` elements, the one at each index `f` of it: `array.init(3, { i -> i * i })`
is `[0, 1, 4]`. A `count` of 0 or less makes an empty array.

## `array.create`

```nanyx fragment
def array.create: [Default(a)] int -> array(a)
```

An array of `count` elements, each the `Default` of its type: `array.create(3)` of ints is
`[0, 0, 0]`. A `count` of 0 or less makes an empty array.

## `array.map`

```nanyx fragment
def array.map: [c] array(a), ([c] a -> b) -> array(b)
```

An array of `f` of each element of `xs`, in order.

## `array.fold`

```nanyx fragment
def array.fold: [c] array(a), b, ([c] (b, a) -> b) -> b
```

`step` applied to `initial` and the first element of `xs`, then to that and the second, and so
on to the last: `array.fold(xs, 0, { total, x -> total + x })` adds them up.

## `array.reduce`

```nanyx fragment
def array.reduce: [c] array(a), ([c] (a, a) -> a) -> #some(a) | #none
```

The elements of `xs` combined with `f`, in order, starting from the first: `Array[1, 2, 3, 4, 5]
\array.reduce { * }` is `#some(120)`, and an empty array, having no first element, gives `#none`.
To start from another value, use [`array.fold`](#arrayfold).

## `array.foldBack`

```nanyx fragment
def array.foldBack: [c] array(a), b, ([c] (b, a) -> b) -> b
```

[`array.fold`](#arrayfold) from the last element to the first, without copying `xs`:
`array.foldBack(Array["a", "b", "c"], "", { acc, x -> acc ++ x })` is `"cba"`.

## `array.foldWhile`

```nanyx fragment
def array.foldWhile: [c] array(a), b, ([c] (b, a) -> #some(b) | r) -> #some(b) | r
```

[`array.fold`](#arrayfold) that can stop part-way: `step` gives `#some(acc)` to go on to the next element,
and anything else it gives ends the fold there and is its result, as it is. A fold that runs to the
end gives `#some(acc)`. `step` isn't called again after it stops:
`array.foldWhile(Array[1, 2, 3, 4, 5], 0, { total, x -> if total + x > 6 then #full(total) else
#some(total + x) })` is `#full(6)`.

## `array.isEmpty`

```nanyx fragment
def array.isEmpty: array(a) -> bool
```

Whether `xs` holds no elements.

## `array.filter`

```nanyx fragment
def array.filter: [c] array(a), ([c] a -> bool) -> array(a)
```

An array of the elements of `xs` that `keep` holds for, in order.

## `array.filterMap`

```nanyx fragment
def array.filterMap: [c] array(a), ([c] a -> #some(b) | #skip) -> array(b)
```

An array of the values `f` gives as `#some`, in order, leaving out the elements it gives `#skip`
for: a `map` and a `filter` in one, which runs `f` once on each element.

## `array.select`

```nanyx fragment
def array.select: [c] array(a), ([c, Emit(b)] a -> ()) -> array(b)
```

An array of the values `f` emits for each element in turn, in the order it emits them, as
[`list.select`](prelude-lists#listselect) gives them: `f` calls `Emit`'s `emit` for each.

## `array.selectOne`

```nanyx fragment
def array.selectOne: [c, d] array(a), ([c] a -> [d] seq(b)) -> #some(b) | #notFound
```

The first value `f` yields, running it on the elements in turn, or `#notFound` when it yields
none. A lambda with `yield` in it is a lazy sequence (see `Yield`), so this stops at the first
yield: `f` runs on no element after the one it yields for.

## `array.find`

```nanyx fragment
def array.find: [c] array(a), ([c] a -> bool) -> #some(a) | #notFound
```

The first element `matches` holds for, or `#notFound`.

## `array.findIndex`

```nanyx fragment
def array.findIndex: [c] array(a), ([c] a -> bool) -> #some(int) | #notFound
```

The position of the first element `matches` holds for, counting from 0, as [`array.at`](prelude-arrays#arrayat) does,
or `#notFound`.

## `array.any`

```nanyx fragment
def array.any: [c] array(a), ([c] a -> bool) -> bool
```

Whether `test` holds for any element (so `false` for an empty array).

## `array.all`

```nanyx fragment
def array.all: [c] array(a), ([c] a -> bool) -> bool
```

Whether `test` holds for every element (so `true` for an empty array).

## `array.contains`

```nanyx fragment
def array.contains: [Eq(a)] array(a), a -> bool
```

Whether an element of `xs` is equal to `x`. Elements are compared with `==`, by their structure.

## `array.reverse`

```nanyx fragment
def array.reverse: array(a) -> array(a)
```

An array of the elements of `xs`, last first.

## `array.sortWith`

```nanyx fragment
def array.sortWith: [c] array(a), ([c] (a, a) -> #less | #equal | #greater) -> array(a)
```

An array of the elements of `xs` in the order `order` gives: `order(a, b)` says whether `a` comes
before `b` (`#less`), after it (`#greater`), or either way (`#equal`), as `compare` does. The sort
is stable: elements `order` finds equal keep their order. It is [`MArray.sortWith`](mslice#marraysortwith)'s merge sort,
on a copy of the elements.

## `array.sortBy`

```nanyx fragment
def array.sortBy: [Ord(b), c] array(a), ([c] a -> b) -> array(a)
```

An array of the elements of `xs` in ascending order of `key`. The sort is stable: elements with
the same key keep their order.

## `array.sort`

```nanyx fragment
def array.sort: [Ord(a)] array(a) -> array(a)
```

An array of the elements of `xs` in ascending order of their `Ord`. The sort is stable.

## `array.take`

```nanyx fragment
def array.take: array(a), int -> array(a)
```

An array of the first `count` elements of `xs` (all of them when there are fewer).

## `array.skip`

```nanyx fragment
def array.skip: array(a), int -> array(a)
```

An array of the elements of `xs` after the first `count`.

## `array.zip`

```nanyx fragment
def array.zip: array(a), array(b) -> array((a, b))
```

An array of pairs of the elements of `xs` and `ys` at the same position, as long as the shorter.

## `array.scan`

```nanyx fragment
def array.scan: [c] array(a), b, ([c] (b, a) -> b) -> array(b)
```

An array of the running totals of a fold: `initial`, then each value [`array.fold`](#arrayfold) passes along,
so the last is what the fold gives back, and it holds one more element than `xs`.

<!-- End of the generated reference. -->