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
Reading one element, [`Slice.at`](#sliceat), gives `#notFound` past the end, never a trap.

A function that makes new elements, such as [`Slice.map`](#slicemap), [`Slice.filter`](#slicefilter) or [`Slice.sort`](#slicesort),
gives them in an `Array` of their own. [`Slice.take`](#slicetake) and [`Slice.skip`](#sliceskip) give a slice of the same
array, as [`Slice.slice`](#sliceslice) does, and copy nothing.

Each function is attached to `Slice`, so with `import nanyx/slice` it is called as
`Slice.length(s)`, or as `s \length`; `xs \slice(1, 4)` makes a slice of the array `xs`, and the
literal `Slice[1, 2, 3]` a slice of all of a new array.

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

## `Slice.fromArray`

```nanyx fragment
def Slice.fromArray: array(a) -> Slice(a)
```

All of `xs`, as a slice that shares its storage. The literal `Slice[1, 2, 3]` is made with it.

## `Slice.fromList`

```nanyx fragment
def Slice.fromList: list(a) -> Slice(a)
```

The elements of `xs`, in order, as a slice of all of a new array.

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
def Slice.at: Slice(a), int -> #some(a) | #notFound
```

The element of `s` at `index`, counting from 0 at the start of `s`, or `#notFound` when `index`
is negative or past its end.

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
def Slice.reduce: [c] Slice(a), ([c] (a, a) -> a) -> #some(a) | #none
```

The elements of `s` combined with `f`, in order, starting from the first: a slice of `1` to `5`
reduced with `{ * }` is `#some(120)`, and an empty slice, having no first element, gives `#none`.
To start from another value, use [`Slice.fold`](#slicefold).

## `Slice.foldBack`

```nanyx fragment
def Slice.foldBack: [c] Slice(a), b, ([c] (b, a) -> b) -> b
```

[`Slice.fold`](#slicefold) from the last element to the first, without copying: a slice of `"a"`, `"b"` and
`"c"` folded back with `{ acc, x -> acc ++ x }` from `""` is `"cba"`.

## `Slice.foldWhile`

```nanyx fragment
def Slice.foldWhile: [c] Slice(a), b, ([c] (b, a) -> #some(b) | r) -> #some(b) | r
```

[`Slice.fold`](#slicefold) that can stop part-way: `step` gives `#some(acc)` to go on to the next element,
and anything else it gives ends the fold there and is its result, as it is. A fold that runs to the
end gives `#some(acc)`. `step` isn't called again after it stops.

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

## `Slice.filter`

```nanyx fragment
def Slice.filter: [c] Slice(a), ([c] a -> bool) -> array(a)
```

An array of the elements of `s` that `keep` holds for, in order.

## `Slice.filterMap`

```nanyx fragment
def Slice.filterMap: [c] Slice(a), ([c] a -> #some(b) | #skip) -> array(b)
```

An array of the values `f` gives as `#some`, in order, leaving out the elements it gives `#skip`
for: a `map` and a `filter` in one walk.

## `Slice.select`

```nanyx fragment
def Slice.select: [c] Slice(a), ([c, Emit(b)] a -> ()) -> array(b)
```

An array of the values `f` emits for each element in turn, in the order it emits them, as
[`list.select`](prelude-lists#listselect) gives them: `f` calls `Emit`'s `emit` for each.

## `Slice.selectOne`

```nanyx fragment
def Slice.selectOne: [c, d] Slice(a), ([c] a -> [d] seq(b)) -> #some(b) | #notFound
```

The first value `f` yields, running it on the elements in turn, or `#notFound` when it yields
none. A lambda with `yield` in it is a lazy sequence (see `Yield`), so this stops at the first
yield: `f` runs on no element after the one it yields for.

## `Slice.find`

```nanyx fragment
def Slice.find: [c] Slice(a), ([c] a -> bool) -> #some(a) | #notFound
```

The first element `matches` holds for, or `#notFound`.

## `Slice.findIndex`

```nanyx fragment
def Slice.findIndex: [c] Slice(a), ([c] a -> bool) -> #some(int) | #notFound
```

The position of the first element `matches` holds for, counting from 0 at the start of `s`, as
[`Slice.at`](#sliceat) does, or `#notFound`.

## `Slice.any`

```nanyx fragment
def Slice.any: [c] Slice(a), ([c] a -> bool) -> bool
```

Whether `test` holds for any element (so `false` for an empty slice).

## `Slice.all`

```nanyx fragment
def Slice.all: [c] Slice(a), ([c] a -> bool) -> bool
```

Whether `test` holds for every element (so `true` for an empty slice).

## `Slice.contains`

```nanyx fragment
def Slice.contains: [Eq(a)] Slice(a), a -> bool
```

Whether an element of `s` is equal to `x`. Elements are compared with `==`, by their structure.

## `Slice.reverse`

```nanyx fragment
def Slice.reverse: Slice(a) -> array(a)
```

An array of the elements of `s`, last first.

## `Slice.sortWith`

```nanyx fragment
def Slice.sortWith: [c] Slice(a), ([c] (a, a) -> #less | #equal | #greater) -> array(a)
```

An array of the elements of `s` in the order `order` gives: `order(a, b)` says whether `a` comes
before `b` (`#less`), after it (`#greater`), or either way (`#equal`), as `compare` does. The sort
is stable: elements `order` finds equal keep their order. It is [`array.sortWith`](array#arraysortwith) of a copy of
the elements.

## `Slice.sortBy`

```nanyx fragment
def Slice.sortBy: [Ord(b), c] Slice(a), ([c] a -> b) -> array(a)
```

An array of the elements of `s` in ascending order of `key`. The sort is stable: elements with the
same key keep their order.

## `Slice.sort`

```nanyx fragment
def Slice.sort: [Ord(a)] Slice(a) -> array(a)
```

An array of the elements of `s` in ascending order of their `Ord`. The sort is stable.

## `Slice.take`

```nanyx fragment
def Slice.take: Slice(a), int -> Slice(a)
```

The first `count` elements of `s` (all of them when there are fewer), as a slice of the same
array: it copies nothing.

## `Slice.skip`

```nanyx fragment
def Slice.skip: Slice(a), int -> Slice(a)
```

The elements of `s` after the first `count`, as a slice of the same array: it copies nothing.

## `Slice.zip`

```nanyx fragment
def Slice.zip: Slice(a), Slice(b) -> array((a, b))
```

An array of pairs of the elements of `xs` and `ys` at the same position, as long as the shorter.

## `Slice.scan`

```nanyx fragment
def Slice.scan: [c] Slice(a), b, ([c] (b, a) -> b) -> array(b)
```

An array of the running totals of a fold: `initial`, then each value [`Slice.fold`](#slicefold) passes along,
so the last is what the fold gives back, and it holds one more element than `s`.

<!-- End of the generated reference. -->
