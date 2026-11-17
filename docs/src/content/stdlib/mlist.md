---
title: mlist
description: Mutable lists that grow at the end
order: 10.8
---

Import it as `nanyx/mlist`. Reach for an `MList` to build up a list inside a `memory` block: `add` puts an element at the end in constant time, and `set` changes one in place. Like an [`MRef`](mref), it belongs to the block it was made in, and can't leave it, so give back `MList.toList` of it.

```nanyx
import nanyx/mlist

def squares = memory {
  def items: MList(int) = MList.empty()
  for 1..4 | n -> items \add(n * n)
  def changed = items \set(0, 0) -- #ok
  MList.toList(items) -- [0, 4, 9, 16]
}
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Mutable lists: lists that grow at the end, changed in place.

An `MList(a)` is a list that [`MList.add`](#mlistadd) grows at the end, in constant time, and whose
elements [`MList.set`](#mlistset) changes in place. It holds its elements in an [`MRef`](mref), so it
belongs to the `memory` block it was made in: every operation requires that block's `Memory`,
and the list can't leave the block, so give back [`MList.toList`](#mlisttolist) of it, which takes no time
when the `MList` isn't used afterwards. [`MList.at`](#mlistat) and [`MList.set`](#mlistset) walk to the element, as
`list.get` does.

Each function is attached to `MList`, so with `import nanyx/mlist` it is called as
`MList.add(items, 5)`, or as `items \add(5)`, and `items += 5` adds too.

## `MList`

```nanyx fragment
type MList(a) = [Memory] private MRef(first: list(a), last: list(a), count: int)
```

## `MList.empty`

```nanyx fragment
def MList.empty: [Memory] () -> MList(a)
```

A new list with nothing in it.

## `MList.fromList`

```nanyx fragment
def MList.fromList: [Memory] list(a) -> MList(a)
```

A new list holding `xs`, in order.

## `MList.fromArray`

```nanyx fragment
def MList.fromArray: [Memory] array(a) -> MList(a)
```

A new list holding `xs`, in order. What a literal builds a list with, `MList[1, 2, 3]`, or
`[1, 2, 3]` where an `MList` is expected.

## `MList.add`

```nanyx fragment
def MList.add: [Memory] MList(a), a -> ()
```

Adds `value` at the end of `items`.

## `` MList.`+=` ``

```nanyx fragment
def MList.`+=`: [Memory] MList(a), a -> ()
```

Adds `value` at the end of `items`, as [`MList.add`](#mlistadd) does: `items += 5`.

## `MList.at`

```nanyx fragment
def MList.at: [Memory] MList(a), int -> #some(a) | #none
```

The element of `items` at `index`, counting from 0, or `#none` when `index` is negative or past
the end.

## `MList.set`

```nanyx fragment
def MList.set: [Memory] MList(a), int, a -> #ok | #outOfRange
```

Changes the element of `items` at `index`, counting from 0, to `value`: `#ok`, or `#outOfRange`
when `index` is negative or past the end, and `items` is left as it was.

## `MList.length`

```nanyx fragment
def MList.length: [Memory] MList(a) -> int
```

How many elements `items` holds.

## `MList.toList`

```nanyx fragment
def MList.toList: [Memory] MList(a) -> list(a)
```

The elements of `items`, in order, as a list. When `items` isn't used afterwards, this takes no
time: the list is the cells `items` built. Otherwise it copies them, so that adding to `items`
later leaves the list as it is.

<!-- End of the generated reference. -->
