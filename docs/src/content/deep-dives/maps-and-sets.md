---
title: "Maps and sets"
description: "How map(k, v) and set(a) are built"
order: 8
---

What maps and sets do is described on [maps and sets](../stdlib/prelude-maps) and in
[Collections](../tour/collections). This page says how the compiler builds them. Their known gaps, what is left and the
open questions are on [Maps and sets](../work-in-progress/maps-and-sets) in Work in progress.

# How it works

- **The types.** `map(k, v)` and `set(a)` are types the compiler knows, as it knows `list(a)`; `Map(k, v)` and
  `Set(a)` are the same types, by their capitalised names (`Nanyx.Compiler/Maps.fs`, `Sets.fs` and
  `BuiltinTypeNames.fs`).
- **Literals.** `[k => v, ...]` with no other expected type is a map: the parser makes it a call to the prelude's
  `__map` with the list of the entries as tuples, `__map([(k, v), ...])` (`MapSyntax` in `Nanyx.Parser/Ast.fs`), so the
  type checker and the backend see an ordinary call. A set literal, or a map literal of an expected type, goes through
  `fromArray` ([Syntax](../tour/collections#syntax)).
- **Keys need `Eq` and `Hash`**, as [Hash(a)](../advanced/contexts#hasha) describes. The hash
  is not used yet: lookups compare with `==`.
- **Calling a map is a lookup.** The type checker types `m(k)` as the call to `map.get` (`inferLookup` in
  `Infer.fs`, which reads a call of an array as `array.at` the same way) and records it in `ReadAs`, and `ExpressionRewrites.writeAsRead` writes it as that call for the backend.
  A call of a field that holds a function, `statement.setInt(position, value)`, is read the same way, as a block that
  keeps the field's value in a local and calls it (`FieldCalls`, [#1513](https://github.com/nanyx-lang/nanyx/issues/1513)).
- **Representation.** A map holds the list of its entries, each key once, in the order the keys were first added, and
  a set the list of its elements. The prelude builds and reads them through four intrinsics,
  `__nanyx_map_of_entries`, `__nanyx_map_entries`, `__nanyx_set_of_elements` and `__nanyx_set_elements`, which the
  backend compiles to nothing: a `map(k, v)` is held as a `list((k, v))`, and a `set(a)` as a `list(a)`.
- **`==` compares contents, in any order** ([#618](https://github.com/nanyx-lang/nanyx/issues/618)). The compiler's
  own comparison, which works by structure, would compare the lists underneath in order, so a map or a set, and any
  structure that holds one, is always compared by an `==` the compiler derives from its parts (`MapShape` and
  `SetShape` in `StructuralInstances.fs`, as for a list whose elements have their own `==`). The derived `==` calls
  the prelude's `__mapEqualsBy` or `__setEqualsBy` with a function that compares two values or elements. It takes time
  in proportion to the product of the sizes, as a lookup does. A type variable nothing solves in the structure, as
  the element of an empty list's type, holds no value, so it is compared as `unit`, which the backend makes it
  (`ContextInstances.derivedComparison`, [#2522](https://github.com/nanyx-lang/nanyx/issues/2522)).
- **A call that needs `Eq` of a type with variables in it**, such as `list.distinct` on a `list(set(a))` inside a
  function that requires `[Eq(a)]`, is passed the derived `==` as a closure over the `Eq(a)` the function was given
  ([#630](https://github.com/nanyx-lang/nanyx/issues/630)).
