---
title: mref
description: Mutable references, cells that are values
order: 10.6
---

Import it as `nanyx/mref`. Reach for an `MRef` over a `mut` cell when the cell has to be a value: held in a record or a list, shared by several holders, or made by one function and used by another. Like a cell, it belongs to the `memory` block it was made in, and can't leave it.

```nanyx
import nanyx/mref

def total = memory {
  def count = MRef.new(0)
  def counters = [count, count]
  for counters | c -> MRef.update(c, { n -> n + 1 })
  MRef.get(count)
}
-- 2
```

Each holder of a reference sees what [`MRef.set`](#mrefset) changes. A reference has no `==`, order, hash or `toString`: compare or write what [`MRef.get`](#mrefget) gives instead. See [Memory](../advanced/memory#references) for how references and cells fit together.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Mutable references: a cell as a value.

An `MRef(a)` holds one value, which [`MRef.set`](#mrefset) changes in place: every holder of the same
reference sees the change. Unlike a `mut` cell, a reference is a value, so it can be held in a
record, a list or another reference, passed without `mut`, and returned from a function. It
belongs to the `memory` block it was made in, as a cell does: making, reading or changing one
requires that block's `Memory`, and it can't leave the block.

A reference has no `==`, order or hash, since what it holds changes: compare what
[`MRef.get`](#mrefget) gives instead. Nor has it a `toString`, since writing what it holds would read it
without `Memory`.

## `MRef`

```nanyx fragment
type MRef(a) = [Memory] private box(a)
```

## `MRef.new`

```nanyx fragment
def MRef.new: [Memory] a -> MRef(a)
```

A new reference holding `value`.

## `MRef.get`

```nanyx fragment
def MRef.get: [Memory] MRef(a) -> a
```

The value `ref` holds now.

## `MRef.set`

```nanyx fragment
def MRef.set: [Memory] MRef(a), a -> ()
```

Changes `ref` to hold `value`, in place of the value it held.

## `MRef.update`

```nanyx fragment
def MRef.update: [Memory, c] MRef(a), ([c] a -> a) -> ()
```

Changes `ref` to hold `f` of the value it holds.

<!-- End of the generated reference. -->
