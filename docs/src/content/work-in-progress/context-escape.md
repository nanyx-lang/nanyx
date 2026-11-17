---
title: "Context escape"
description: "What is still open about scoped contexts and naming the instance a lambda is given"
order: 1
---

Which values may leave the scope that supplies a context, and how a `use` implements one context in terms of another,
were decided in [#1035](https://github.com/nanyx-lang/nanyx/issues/1035) and [#1141](https://github.com/nanyx-lang/nanyx/issues/1141),
and are built. The language docs describe them: [Contexts](../advanced/contexts) for discharging at a `use`, handlers,
naming the instance a lambda is given, contexts not being types, protected and private contexts and scoped contexts;
[Memory](../advanced/memory#values-cannot-escape-memory-scope) for escape from a `memory` block; and
[Workflows](../advanced/workflows) for builders. NYX0233 is the error for a value that leaves its scope. This page keeps
what is still open.

# Writing a type at two scopes

Where one type requires a scoped context at more than one scope, the compiler numbers them in order of first
appearance, `([Memory₁] () -> a), ([Memory₂] () -> b) -> b`, in hover, inlay hints, error messages and `nanyx check`;
at one scope it is plain `[Memory]` ([#1553](https://github.com/nanyx-lang/nanyx/issues/1553)). No source can write
the numbered form yet, so a function whose type needs it can't write its type, and the language server's Write the
inferred type refuses such a type. How to write it is undecided
([#1591](https://github.com/nanyx-lang/nanyx/issues/1591)).

# Naming a context a lambda is given

`context x` before a lambda's parameters names a plain context whose members the lambda's signature binds, or the
`Memory` of a `memory` block. Any other context without members, and any context with parameters, can't be named
yet: `{ context p -> ... }` in a `def g: [Show(int)] () -> ()` is NYX0246, as if the lambda were given no context.
