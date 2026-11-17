---
title: "The bottom type"
description: "What is still open about `undefined`, the type of an expression that never gives a value"
order: 23
---

`undefined` is built. The language docs describe it in [Top and bottom types](../tour/types#top-and-bottom-types),
`crash` in [Error handling](../tour/error-handling), and the `unreachableCode` rule in the
[official linter](../standard-tools/official-linter#unreachable-code-path). This page keeps what is still open.

# Open questions

- **Arguments after one that never returns.** In `f(crash("a"), g())`, `g()` never runs. The `unreachableCode` rule
  only looks at the statements of blocks.
- **Inferred divergence.** A function that never returns but is not annotated, such as `def loop = { x -> loop(x) }`,
  is inferred as `a -> b`, not `a -> undefined`, so calls to it are not seen as diverging.
- **`Equal` constraints.** `undefined` is assignable to every type under an `Assignable` constraint, but an `Equal`
  constraint treats it as the empty union it is, so it does not agree with an `int` there. Whether other `Equal`
  constraints should let it through too is open.
