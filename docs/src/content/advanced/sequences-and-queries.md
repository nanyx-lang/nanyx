---
title: "Sequences and queries"
description: "Seq vs gen and querying patterns"
order: 6
---

> **Being redesigned.** [Lazy sequences](../work-in-progress/lazy-sequences) proposes one lazy type, `seq(a)`, in
> place of the `seq`/`gen` split this page describes.

Nanyx has two enumerable abstractions: `seq(a)` and `gen(a)`.

- A `seq` is known to be finite and safe to iterate.
- A `gen` (generator) can be infinite, lazy, or non-deterministic.

# Iteration safety

```nanyx
for 0.. | i -> println("Hello") -- Error: cannot iterate over a gen

for 1..10 | i -> println("Hello") -- OK

for 1.. \take(10) | i -> println("Hello") -- OK: take turns a gen into a seq
```

# Enumerating with indexes

A `gen` may be zipped with a `seq` in a `for` loop, because the finite side bounds the iteration:

```nanyx
def items: seq(string) = ...

for items, 0.. | item, i -> println("Item {i} is {item}")
```

# Iterators

Both `seq` and `gen` provide `getIterator`. The main difference is that a `gen` iterator can be impure, while a `seq` iterator is pure, reflecting the finiteness guarantee.
