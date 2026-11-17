---
title: "Maps and sets"
description: "How map(k, v) and set(a) are built, and what maps and sets still need"
order: 10
---

> **Status: implemented, keyed by `Eq` and `Hash`, but not yet hash-based.** `map(k, v)`, map literals, lookups and
> `list.groupBy` came with [#477](https://github.com/nanyx-lang/nanyx/issues/477); the `Hash` context, keys that
> require it, and `set(a)` with [#533](https://github.com/nanyx-lang/nanyx/issues/533). What works is described on
> [maps and sets](../stdlib/prelude-maps) and in [Collections](../tour/collections). Both still hold a list, so a
> lookup takes time in proportion to the size. The [open questions](#open-questions) need the maintainer's answer; the
> defaults the code takes until then are marked.

# How it works

- **The types.** `map(k, v)` and `set(a)` are types the compiler knows, as it knows `list(a)`; `Map(k, v)` and
  `Set(a)` are the same types, by their capitalised names (`Nanyx.Compiler/Maps.fs`, `Sets.fs` and
  `BuiltinTypeNames.fs`).
- **Literals.** `[k => v, ...]` with no other expected type is a map: the parser makes it a call to the prelude's
  `__map` with the list of the entries as tuples, `__map([(k, v), ...])` (`MapSyntax` in `Nanyx.Parser/Ast.fs`), so the
  type checker and the backend see an ordinary call. A set literal, or a map literal of an expected type, goes through
  `fromArray` ([Collection literals](./collection-literals)).
- **Keys need `Eq` and `Hash`**, as [Hash(a)](../advanced/contexts#hasha) describes. The hash
  is not used yet: lookups compare with `==`.
- **Calling a map is a lookup.** The type checker types `m(k)` as the call to `map.get` (`inferMapLookup` in
  `Infer.fs`) and records it in `ReadAs`, and `ExpressionRewrites.writeAsRead` writes it as that call for the backend.
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
  in proportion to the product of the sizes, as a lookup does.
- **A call that needs `Eq` of a type with variables in it**, such as `list.distinct` on a `list(set(a))` inside a
  function that requires `[Eq(a)]`, is passed the derived `==` as a closure over the `Eq(a)` the function was given
  ([#630](https://github.com/nanyx-lang/nanyx/issues/630)).

# Known gaps

- **Comparing maps in a local function.** A local function that compares values generic in their type is passed the
  compiler's own `==`, which cannot compare a map, so it is an error when the type holds a map or a set.
- **No order or hash of maps and sets.** `<` on a map or a set, or on anything holding one, is an error, and so is a
  map or a set as a key or a set's element: hashing the list underneath would give equal maps different hashes.
- **Tag keys of different names.** An unannotated chain of inserts whose keys are tags of different names is a type
  error: the keys are not joined. A literal, `[#red => 1, #green => 2]`, is a `map(#red | #green, int)`, as a list
  literal's items are joined, and writing the key type works.

# What is left: hash-based maps

The list of entries is to become a persistent hash trie written in the prelude, behind the same intrinsics, so
nothing outside the prelude changes. A trie is a recursive type, `#leaf(...) | #node(Trie(k, v), Trie(k, v))`, which a
type alias can now be ([#246](https://github.com/nanyx-lang/nanyx/issues/246)). This waits on
[open question 2](#open-questions), which decides the order a hash-based map gives its entries in.

# Open questions

These are the choices the docs leave open. The code takes the marked default until they are answered.

1. **A key written twice in a literal.** `["a" => 1, "a" => 2]`.
   - *(Default)* The later value wins, as in `map.fromList`, JavaScript, Python and F#.
   - An error, or a warning, when two keys are the same constant. Catches typos, but only for constant keys.
   - **Recommended:** the later value wins, plus a warning for two equal constant keys, added with the linters.
2. **The order of a map's entries.** What `map.toList` and `for` give, and so what `==` and `hash` on maps see.
   - *(Default, while maps hold a list)* The order the keys were first added, which a list gives for free.
   - The order of the keys' hashes. Deterministic for a given set of keys whatever order they were added in, so
     equal maps have one form, and `hash` on maps can be derived from structure, and `==` could compare in order
     rather than by lookups. The order is meaningless to a reader, and changes if `hash` does.
   - Insertion order kept by a hash map (Python's `dict`). Predictable, but a persistent version needs a second
     index, and equal maps still differ in order.
   - **Recommended:** the order of the keys' hashes once maps are hash tries, documented as unspecified, with
     `map.toList \list.sortBy(...)` for a chosen order.
3. **Is a map a function?** Today a map can be *called*, but it is not a function value: it cannot be passed where a
   function is expected, or annotated with a function type.
   - *(Default)* Not a function: write `{ k -> m(k) }` where one is needed.
   - A map is assignable to its lookup's function type: the type checker accepts a `map(k, v)` where a
     `k -> #some(v) | #notFound` is wanted, and the backend wraps it in a closure there.
   - **Recommended:** make a map assignable to its lookup's type.

Settled:

- **Spelling.** The type is `map(k, v)`, and its functions are attached to it by that name, `map.get`, as `list.map`
  is to `list(a)` (#653).
- **Builders.** A literal builds any type with an attached `fromArray`
  ([#1543](https://github.com/nanyx-lang/nanyx/issues/1543), [Collection literals](./collection-literals)), so `[]`
  with an expected map type is an empty map, and `set[1, 2]` is a set.
