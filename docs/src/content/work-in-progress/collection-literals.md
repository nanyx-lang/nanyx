---
title: "Collection literals"
description: "What is still open about square-bracket literals of types with a fromArray"
order: 11
---

A square-bracket literal builds any type with an attached `fromArray`, chosen by the expected type
(`def a: Array(int) = [1, 2]`) or by the type's name before the brackets (`Array[1, 2]`), as the tour's
[Collections](../tour/collections#syntax) describes. It was done in [#1543](https://github.com/nanyx-lang/nanyx/issues/1543).
This page keeps what is still open.

# Still open

- **A `FromArray` context.** The issue suggested reading `fromArray` through a prelude context synthesised from it, as
  `ToString` is from `toString`, so that generic code could build literals of a type variable. Literals call the
  attached `fromArray` directly instead, and a literal expected as a type variable stays a list.
- **Where the expected type comes from.** Only a def's written type (on its line, or ending its block) and a call's
  parameter: a lambda's result, a record field's type and a pattern's don't make a literal another type yet, so
  `def mk: () -> Array(int) = { -> [1, 2] }` is a type error. A literal that is only a spread, `[...xs]`, is parsed as
  `xs`, so it is never read as another type.
- **Spreading a set, a `seq` or a slice** waits for an `Iterable` context ([Lazy sequences](./lazy-sequences),
  [#1802](https://github.com/nanyx-lang/nanyx/issues/1802)). Spreading an array or a list works.
