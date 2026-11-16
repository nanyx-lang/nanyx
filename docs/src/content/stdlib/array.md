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
println(xs \map({ x -> x / 10 }))
-- 3
-- #some(20)
-- #none
-- #some(30)
-- [1, 2, 3]
```

An array is callable: `xs(i)` is `array.at(xs, i)`, so it gives `#some(a) | #none` and never traps. Use it as `xs(0) ?? fallback`. `array.at` is in the prelude, so a call of an array works in a module that doesn't import `nanyx/array`.

`array.create(n)` fills each slot with the [`Default`](prelude-contexts#defaults) of the element type, so it needs to know that type: `def zeros: Array(int) = array.create(3)`. `==` compares two arrays element by element. An array has no order and no hash, so it can't be sorted against another, or put in a set or used as a map's key.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Arrays: immutable, fixed-length sequences with constant-time indexing.

An `Array(a)` holds its elements in one block, so [`array.at`](prelude-arrays#arrayat) reads any of them in the same time,
where `at` on a list walks to it. Nothing about an array changes once it is made, so it needs no
`Memory`. There is no literal for one: make it from a list with [`array.fromList`](#arrayfromlist), from its
indices with [`array.init`](#arrayinit), or filled with each slot's default with [`array.create`](#arraycreate).

Each function is attached to `array`, so it is in scope wherever arrays are, without an import, and is called as
`array.length(xs)`, or as `xs \length` when `xs` is known to be an array. `==` compares two arrays
element by element, and an array is written as a list is, `[10, 20, 30]`.

## `array.fromList`

```nanyx fragment
def array.fromList: list(a) -> array(a)
```

An array of the elements of `xs`, in order.

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

<!-- End of the generated reference. -->