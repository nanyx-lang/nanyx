---
title: "Stack vs heap allocation"
description: "Which values Nanyx keeps on the heap, and what makes that cheap"
order: 4
---

You never choose where a value lives: there is nothing to annotate. The rule is simple, and the same for every program
the WebAssembly backend compiles.

# What lives where

- **Numbers, booleans and tags without a payload**, such as `#none`, are never on the heap. They are WebAssembly
  locals and values: an `int` or a `U64` is an `i64`, a `float` an `f64`, and a tag without a payload is its code in an
  `i32`.
- **Every compound value is on the heap**: a string, a list cell, a record, a tuple, a closure, and a tag with a
  payload. So is the `#some` that a lookup gives back, and a small tuple a function returns.
- **A `mut` cell** that no lambda captures is a local. One a lambda captures, or one passed with `mut`, is a box on the
  heap, and so is every reference (`MRef`); see [Memory](./memory).

A compound value is passed and stored as a pointer to it. Since values are immutable, that can't be told apart from
copying it, so there is no choice to make between passing by value and passing by reference.

WebAssembly's own stack holds only numbers, and the backend keeps no stack of its own in memory, so a record or a tuple
is never placed in a function's frame.

# What makes the heap cheap

- **Freed at once.** A value on the heap is freed as soon as nothing refers to it, by
  [reference counting](../deep-dives/reference-counting), with no garbage collector and no pauses.
- **Reused in place.** When a `match` takes apart a value that nothing else refers to and builds a new one of the same
  size, the new value is written into the old one's memory. Mapping over a list you hold the only reference to, or
  updating a record with `with`, allocates nothing ([Reuse in place](../deep-dives/reference-counting#reuse-in-place)).
- **Known layouts.** Generic code is compiled once for each set of types it is used at, so the compiler always knows
  which fields of a value are on the heap, and an `int` keeps its full width rather than being boxed.

How values are laid out, and the allocator, are in [Memory management](../deep-dives/memory-management).

# Work on allocation

- Returning a tuple, or a tag's payload, without allocating it
  ([#2312](https://github.com/nanyx-lang/nanyx/issues/2312)).
- A tag with several payload values as one allocation, reused when a `match` rebuilds it
  ([#1296](https://github.com/nanyx-lang/nanyx/issues/1296)).
