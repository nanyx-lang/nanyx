---
title: marray
description: Mutable arrays, fixed-length and changed in place
order: 10.55
---

Import it as `nanyx/marray`. Reach for an `MArray` inside a `memory` block when you change elements by index: `set` changes one in place, and `at` reads any of them in the same time. It has the length it was made with for good. Like an [`MRef`](mref), it belongs to the block it was made in and can't leave it, so give back an [`Array`](array) copy of it.

```nanyx
import nanyx/marray

def xs = memory {
  def ys = MArray.init(3, { i -> i * 10 })
  def changed = ys \set(1, 99) -- #ok
  MArray.toArray(ys) -- [0, 99, 20]
}
```

`MArray.create(n)` fills each slot with the [`Default`](prelude-contexts#defaults) of the element type, so it needs to know that type: `def zeros: MArray(int) = MArray.create(3)`. `set` and `swap` give `#ok`, or `#outOfRange` for an index out of range, and leave the array as it was.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Mutable arrays: fixed-length arrays whose elements are changed in place.

An `MArray(a)` holds its elements in one block, as an `Array` does, so [`MArray.at`](#marrayat) reads any of
them in the same time, and [`MArray.set`](#marrayset) changes one in place. It has the length it was made
with for good. It belongs to the `memory` block it was made in: every operation requires that
block's `Memory`, and the array can't leave the block, so give back [`MArray.toArray`](#marraytoarray) of it.

[`MArray.fromArray`](#marrayfromarray) and [`MArray.toArray`](#marraytoarray) copy, so an `Array` never changes, whatever happens
to the `MArray` it was copied into or from. An index out of range is `#none` or `#outOfRange`,
never a trap. A mutable array has no `==`, order, hash or `toString`: compare or write what
[`MArray.toArray`](#marraytoarray) gives instead.

Each function is attached to `MArray`, so with `import nanyx/marray` it is called as
`MArray.set(xs, 1, 99)`, or as `xs \set(1, 99)`.

## `MArray`

```nanyx fragment
type MArray(a) = [Memory] private marray(a)
```

## `MArray.init`

```nanyx fragment
def MArray.init: [Memory, c] int, ([c] int -> a) -> MArray(a)
```

A new array of `count` elements, the one at each index `f` of it: `MArray.init(3, { i -> i * i })`
holds `0`, `1` and `4`. A `count` of 0 or less makes an empty array.

## `MArray.create`

```nanyx fragment
def MArray.create: [Memory, Default(a)] int -> MArray(a)
```

A new array of `count` elements, each the `Default` of its type: `MArray.create(3)` of ints
holds three `0`s. A `count` of 0 or less makes an empty array.

## `MArray.fromList`

```nanyx fragment
def MArray.fromList: [Memory] list(a) -> MArray(a)
```

A new array of the elements of `xs`, in order.

## `MArray.fromSeq`

```nanyx fragment
def MArray.fromSeq: [Memory, c] ([c] seq(a)) -> MArray(a)
```

A new array of the elements of `s`, pulled now, in order. It requires the contexts the
sequence's steps do.

## `MArray.build`

```nanyx fragment
def MArray.build: [Memory, c, d] ([c] () -> [d] seq(a)) -> MArray(a)
```

A new array of what `block` yields. The same as `seq(block) \MArray.fromSeq`.

## `MArray.fromArray`

```nanyx fragment
def MArray.fromArray: [Memory] array(a) -> MArray(a)
```

A new array of a copy of the elements of `xs`, in order: changing it leaves `xs` as it is.

## `MArray.toArray`

```nanyx fragment
def MArray.toArray: [Memory] MArray(a) -> array(a)
```

A copy of the elements of `xs` now, in order, as an `Array`, which later changes to `xs` leave
as it is.

## `MArray.toSeq`

```nanyx fragment
def MArray.toSeq: [Memory] MArray(a) -> seq(a)
```

The elements of `xs` now, in order, as a sequence: changing `xs` later doesn't change it. A
mutable array is `Iterable` by it, so a `for` loop walks one.

## `MArray.toList`

```nanyx fragment
def MArray.toList: [Memory] MArray(a) -> list(a)
```

The elements of `xs` now, in order, as a list.

## `MArray.reduce`

```nanyx fragment
def MArray.reduce: [Memory, c] MArray(a), ([c] (a, a) -> a) -> #some(a) | #none
```

The elements of `xs` combined with `f`, in order, starting from the first: an `MArray` of `1` to
`5` reduced with `{ * }` is `#some(120)`, and an empty one, having no first element, gives
`#none`. To start from another value, use [`list.fold`](prelude-lists#listfold).

## `MArray.length`

```nanyx fragment
def MArray.length: [Memory] MArray(a) -> int
```

How many elements `xs` holds.

## `MArray.at`

```nanyx fragment
def MArray.at: [Memory] MArray(a), int -> #some(a) | #none
```

The element of `xs` at `index`, counting from 0, or `#none` when `index` is negative or past the
end.

## `MArray.set`

```nanyx fragment
def MArray.set: [Memory] MArray(a), int, a -> #ok | #outOfRange
```

Changes the element of `xs` at `index`, counting from 0, to `value`: `#ok`, or `#outOfRange`
when `index` is negative or past the end, and `xs` is left as it was.

## `MArray.swap`

```nanyx fragment
def MArray.swap: [Memory] MArray(a), int, int -> #ok | #outOfRange
```

Swaps the elements of `xs` at `i` and `j`: `#ok`, or `#outOfRange` when either is negative or
past the end, and `xs` is left as it was.

<!-- End of the generated reference. -->
