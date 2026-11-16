---
title: mslice
description: Mutable slices, views of part of a mutable array, changed in place
order: 10.56
---

Import it as `nanyx/mslice`. Reach for an `MSlice` to change part of an [`MArray`](marray) in place: `ys \slice(0, 3) \sort` sorts its first three elements and leaves the rest. Like the array it views, a slice belongs to the `memory` block it was made in, and can't leave it.

```nanyx
import (
  nanyx/marray
  nanyx/mslice
)

def sorted = memory {
  def ys = MArray.fromArray(Array[5, 3, 9, 1])
  MSlice.sort(ys \slice(0, 3))
  MArray.toArray(ys) -- [3, 5, 9, 1]
}
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Mutable slices: views of part of a mutable array, changed in place.

An `MSlice(a)` is a view of a run of an [`MArray`](marray)'s elements: the array, where the run
starts and how long it is. Making one with [`MArray.slice`](#marrayslice), or a slice of a slice, copies
nothing. Setting an element of a slice, or sorting, filling or reversing it, changes the array it
views, and only the run the slice views.

A slice belongs to the `memory` block of the array it views, as the array does: its declaration
says so, `type MSlice(a) = [Memory] private (...)`, every operation requires the block's
`Memory`, and the slice can't leave the block.

Bounds clamp, as an [`Slice`](slice)'s do: `MArray.slice(xs, from, to)` is the elements from
`from` up to but not including `to`, each moved into `0` to the length. Reading or setting one
element gives `#none` or `#outOfRange` past the end, never a trap.

Each function is attached to `MSlice`, so with `import nanyx/mslice` it is called as
`MSlice.sort(s)`, or as `s \sort`; `xs \slice(0, 3)` makes a slice of the mutable array `xs`.

## `MSlice`

```nanyx fragment
type MSlice(a) = [Memory] private (items: MArray(a), start: int, length: int)
```

## `MArray.slice`

```nanyx fragment
def MArray.slice: [Memory] MArray(a), int, int -> MSlice(a)
```

The elements of `xs` from `from` up to but not including `to`, sharing `xs`'s storage, so that a
change to one shows in the other: each bound is moved into `0` to the length of `xs`.

## `MSlice.length`

```nanyx fragment
def MSlice.length: [Memory] MSlice(a) -> int
```

How many elements `s` views.

## `MSlice.at`

```nanyx fragment
def MSlice.at: [Memory] MSlice(a), int -> #some(a) | #none
```

The element of `s` at `index` now, counting from 0 at the start of `s`, or `#none` when `index`
is negative or past its end.

## `MSlice.set`

```nanyx fragment
def MSlice.set: [Memory] MSlice(a), int, a -> #ok | #outOfRange
```

Changes the element of `s` at `index`, counting from 0 at the start of `s`, to `value`, in the
array `s` views: `#ok`, or `#outOfRange` when `index` is negative or past the end of `s`, and
nothing changes.

## `MSlice.swap`

```nanyx fragment
def MSlice.swap: [Memory] MSlice(a), int, int -> #ok | #outOfRange
```

Swaps the elements of `s` at `i` and `j`: `#ok`, or `#outOfRange` when either is negative or past
the end of `s`, and nothing changes.

## `MSlice.slice`

```nanyx fragment
def MSlice.slice: [Memory] MSlice(a), int, int -> MSlice(a)
```

The elements of `s` from `from` up to but not including `to`, counting from the start of `s`,
sharing the same storage. The bounds clamp into `s`.

## `MSlice.fill`

```nanyx fragment
def MSlice.fill: [Memory] MSlice(a), a -> ()
```

Sets every element of `s` to `value`.

## `MSlice.reverse`

```nanyx fragment
def MSlice.reverse: [Memory] MSlice(a) -> ()
```

Reverses the order of the elements of `s`, in place.

## `MSlice.sort`

```nanyx fragment
def MSlice.sort: [Memory, Ord(a)] MSlice(a) -> ()
```

Sorts the elements of `s` from least to greatest, in place, keeping elements that are equal in
the order they were in. It is an insertion sort, which suits the short runs a slice is often of.

## `MSlice.toList`

```nanyx fragment
def MSlice.toList: [Memory] MSlice(a) -> list(a)
```

The elements of `s` now, in order, as a list.

## `MSlice.toArray`

```nanyx fragment
def MSlice.toArray: [Memory] MSlice(a) -> array(a)
```

A copy of the elements of `s` now, in order, as an `Array`, which later changes to the array `s`
views leave as it is.

<!-- End of the generated reference. -->
