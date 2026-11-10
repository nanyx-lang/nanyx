---
title: "Stack vs heap allocation"
description: "How Nanyx chooses allocation and passing strategy"
order: 4
---

Nanyx handles object allocation automatically. You write values normally, and the compiler decides whether each object is placed on the stack or on the heap.

When an object is stack-allocated, Nanyx passes it either by value or by reference based on its size.

# What this means today

- Allocation strategy is compiler-managed.
- Stack-allocated objects may be passed by value or by ref depending on size.
- You do not currently annotate values to force a specific strategy.

In the WebAssembly backend today, numbers, booleans and tags without a payload are never on the heap, and every string, list cell, record, closure and tag with a payload is. A value on the heap is freed as soon as nothing refers to it, and can be reused in place for a new one: see [Reference counting](../foundations/reference-counting).

# Planned configurability

Nanyx is planned to expose controls so you can choose allocation and passing behavior explicitly when needed, including:

- forcing a copy,
- forcing a reference,
- forcing stack allocation,
- forcing heap allocation.

Until these controls land, allocation and passing decisions remain automatic.

For a broader overview, see [Compiler optimisations](../foundations/compiler-optimisations).
