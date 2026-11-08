---
title: "Compiler optimisations"
description: "How Nanyx improves generated code"
order: 11
---

Nanyx applies compiler optimisations automatically during compilation. These optimisations are intended to make programs faster and smaller without changing program meaning.

# Optimisation model

Nanyx optimisations are implementation details of the compiler pipeline. You should treat them as performance improvements, not language-level guarantees.

In practice this means:

- Your code should remain correct even if a specific optimisation does not apply.
- The exact set of optimisations can evolve between compiler versions.
- Optimisations must preserve observable behaviour.

Tail calls are the exception: a call in tail position never uses up stack. That is a guarantee of the language, not an optimisation, and every compiler version keeps it. See [Tail recursion](../tour/functions#tail-recursion).

# Typical optimisation areas

Depending on code shape and target backend, the compiler may perform optimisations such as:

- constant folding and simplification,
- inlining of small expressions,
- dead-code elimination,
- copy elision and reduced temporary allocations,
- improved passing strategy for values (for example by-value vs by-reference decisions).

For allocation-specific behavior, see [Stack vs heap allocation](../advanced/stack-vs-heap-allocation).

# Writing optimisation-friendly Nanyx

You usually get the best results by writing clear, idiomatic code first:

- Prefer straightforward expressions over clever micro-optimised patterns.
- Keep data flow explicit (especially around large records).
- Profile real workloads before changing code for performance.

# Future direction

Nanyx is expected to expose more user control over selected performance behaviors over time (for example allocation and passing strategy controls), while keeping automatic optimisation as the default experience.
