---
title: "Memory management"
description: "What is still to do and open about reference counting, value layout and memory reuse"
order: 25
---

This page keeps what is still to do and open about how the WebAssembly backend lays values out, counts references and
reuses memory. How it works today is in [Memory management](../compiler-internals/memory-management) in the Compiler
internals section, and what a user sees in [Reference counting](../foundations/reference-counting). It is part of the
[compilation](./compilation) workstream, and its issues carry the `compilation` and `wasm backend` labels.

# Open questions

- **Code size.** Monomorphisation multiplies generic functions; how much does that grow programs, and when should
  the boxing fallback kick in?
- **Should reuse be visible?** Koka's `fip` annotation lets a function promise it runs fully in place, and the
  compiler checks it. That would suit Nanyx's stance on predictable performance.
- **A benchmark for reuse.** The examples are small and mostly build strings; a benchmark that transforms large data
  structures would show what reuse is worth.
- **The size word.** Each block keeps its size in a word before it, though the drop functions know each object's size
  from its type. Dropping it would save 4 bytes an object, but tie `free` to types.
- **Small free blocks and shrinking.** Small free blocks are never joined, with each other or with large ones, and
  memory once grown is never given back to the host. Whether either is worth doing is open.
