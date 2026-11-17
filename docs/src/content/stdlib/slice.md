---
title: slice
description: Slices, views of part of an array that share its storage
order: 10.52
---

Import it as `nanyx/slice`. Reach for a `Slice` to work on part of an array without copying it: `xs \slice(1, 4)` views the elements from index 1 up to 4, and a slice of a slice copies nothing either. `Slice.toArray` copies the run, which lets go of the larger array.

```nanyx
import nanyx/slice

def xs = Array[1, 2, 3, 4, 5]
def middle = xs \slice(1, 4) -- 2, 3, 4
def first = Slice.at(middle, 0) -- #some(2)

println(Slice.length(middle))
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Slices: views of part of an array, which share its storage instead of copying it.

A `Slice(a)` is a view of a run of an `Array(a)`'s elements: the array, where the run starts and
how long it is. Making one with [`array.slice`](#arrayslice), or a slice of a slice, copies nothing and takes
the same time however long the run is. A slice holds the array it views, so the array stays
alive as long as the slice is used, and [`Slice.toArray`](#slicetoarray) copies the run into an array of its
own, which lets go of the larger one.

Bounds clamp: `array.slice(xs, from, to)` is the elements from `from` up to but not including
`to`, each bound moved into `0` to the length, and an empty slice when `to` is before `from`.
Reading one element, [`Slice.at`](#sliceat), gives `#none` past the end, never a trap.

Each function is attached to `Slice`, so with `import nanyx/slice` it is called as
`Slice.length(s)`, or as `s \length`; `xs \slice(1, 4)` makes a slice of the array `xs`.

## `Slice`

```nanyx fragment
type Slice(a) = private (items: array(a), start: int, length: int)
```

## `array.slice`

```nanyx fragment
def array.slice: array(a), int, int -> Slice(a)
```

The elements of `xs` from `from` up to but not including `to`, sharing `xs`'s storage: each bound
is moved into `0` to the length of `xs`, so `xs \slice(1, 100)` is everything after the first.

## `Slice.ofArray`

```nanyx fragment
def Slice.ofArray: array(a) -> Slice(a)
```

All of `xs`, as a slice that shares its storage.

## `Slice.length`

```nanyx fragment
def Slice.length: Slice(a) -> int
```

How many elements `s` views.

## `Slice.isEmpty`

```nanyx fragment
def Slice.isEmpty: Slice(a) -> bool
```

Whether `s` views no elements.

## `Slice.at`

```nanyx fragment
def Slice.at: Slice(a), int -> #some(a) | #none
```

The element of `s` at `index`, counting from 0 at the start of `s`, or `#none` when `index` is
negative or past its end.

## `Slice.slice`

```nanyx fragment
def Slice.slice: Slice(a), int, int -> Slice(a)
```

The elements of `s` from `from` up to but not including `to`, counting from the start of `s`,
sharing the same storage. The bounds clamp into `s`, as [`array.slice`](#arrayslice)'s do.

## `Slice.fold`

```nanyx fragment
def Slice.fold: [c] Slice(a), b, ([c] (b, a) -> b) -> b
```

`step` applied to `initial` and the first element, then to that and the second, and so on: the
last result, or `initial` for an empty slice.

## `Slice.reduce`

```nanyx fragment
def Slice.reduce: [Default(a), c] Slice(a), ([c] (a, a) -> a) -> a
```

The elements of `s` combined with `f`, in order, starting from the element type's `Default`: a
slice of `1`, `2` and `3` reduced with `{ + }` is `6`. An empty one gives the default, so `reduce`
never fails, unlike a `reduce` that starts from the first element. To start from another value,
use [`Slice.fold`](#slicefold).

## `Slice.toSeq`

```nanyx fragment
def Slice.toSeq: Slice(a) -> seq(a)
```

The elements of `s`, in order, each read when the sequence is pulled. A slice is `Iterable` by
it, so a `for` loop walks one.

## `Slice.toList`

```nanyx fragment
def Slice.toList: Slice(a) -> list(a)
```

The elements of `s`, in order, as a list.

## `Slice.toArray`

```nanyx fragment
def Slice.toArray: Slice(a) -> array(a)
```

A copy of the elements of `s`, in order, as an array of its own, which doesn't keep the array
`s` views alive.

## `Slice.map`

```nanyx fragment
def Slice.map: [c] Slice(a), ([c] a -> b) -> array(b)
```

An array of `f` applied to each element of `s`, in order.

<!-- End of the generated reference. -->
