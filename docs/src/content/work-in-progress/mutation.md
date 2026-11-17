---
title: "Mutation"
description: "What is still planned for mutation under Memory"
order: 2
---

Local cells, cell parameters, `while`, and the mutable values `MRef`, `MArray`, `MSlice`, `MStack` and `MList` are
built ([#756](https://github.com/nanyx-lang/nanyx/issues/756), [#1072](https://github.com/nanyx-lang/nanyx/issues/1072),
[#957](https://github.com/nanyx-lang/nanyx/issues/957), [#1491](https://github.com/nanyx-lang/nanyx/issues/1491) to
[#1493](https://github.com/nanyx-lang/nanyx/issues/1493), [#1545](https://github.com/nanyx-lang/nanyx/issues/1545)).
[Memory](../advanced/memory), [Loops](../tour/loops) and [Collections](../tour/collections#arrays-and-slices) describe
them. This page keeps what is still planned.

# Planned

- **`mut` record fields.** The parser reads `mut` before a field name in a record type; nothing checks or compiles it
  yet, so `type Rec = (x: int, mut y: int)` is accepted and means a plain field.
- **Spreading a slice into a literal,** `Array[...s]`, waits for the `Iterable` context that spreading a set does
  ([Collection literals](./collection-literals)).
- **A growable `MArray`.** `MList` is a linked list of cells, so `at` and `set` walk to the element; it can move onto a
  growable `MArray` later without changing its interface.
