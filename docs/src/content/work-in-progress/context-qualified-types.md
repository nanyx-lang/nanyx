---
title: "Context-qualified types"
description: "What is still open about types that carry contexts"
order: 1
---

Decided by the maintainer in [#1059](https://github.com/nanyx-lang/nanyx/issues/1059): **a type carries contexts only
when it's a function type, or when its declaration says so.** Both forms of declaration are built: a type in a scoped
context's scope ([#1521](https://github.com/nanyx-lang/nanyx/issues/1521)) and a type generic in contexts
([#1525](https://github.com/nanyx-lang/nanyx/issues/1525)). [Contexts](../advanced/contexts#contexts-are-part-of-the-type)
describes them, with [Types in a scope](../advanced/contexts#types-in-a-scope) and
[Types generic in contexts](../advanced/contexts#types-generic-in-contexts). This page keeps the two gaps left.

# Still open

- **A type in a scope imported under an alias.** A type in a scope is matched to its context by the context's name
  where it's declared, so a module that imports both under an alias, `t.Cursor` and `[t.Tx]`, gets a `Cursor` whose
  written scope is not the signature's `t.Tx`.
- **Adapting a generic def's result.** A value of a type generic in contexts, given where one that carries more
  contexts is expected, is adapted by the backend (`CoreIrLowerMembers.coerce`): passed as an argument, as a def's
  result read from its declared type, and named as a top-level value. A generic def's result is still read from its
  value's type, so one whose value carries fewer contexts than it declares isn't adapted.
