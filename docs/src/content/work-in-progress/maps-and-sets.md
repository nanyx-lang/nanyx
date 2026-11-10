---
title: "Maps and sets"
description: "The map(k, v) type, map literals, and what maps and sets still need"
order: 10
---

> **Status: maps implemented, sets and hashing not yet.** `map(k, v)`, map literals, lookups and `List.groupBy` are
> in ([#477](https://github.com/nanyx-lang/nanyx/issues/477)). The `Hash` context, hash-based maps and a `Set` are
> [#533](https://github.com/nanyx-lang/nanyx/issues/533). The [open questions](#open-questions) need the
> maintainer's answer; the defaults the code takes until then are marked.

# What is implemented

- **The type.** `map(k, v)` is a type the compiler knows, as it knows `list(a)`; `Map(k, v)` is the same type, as
  `List(a)` is `list(a)` (`Nanyx.Compiler/Maps.fs`). The functions on it are attached to `Map` in the prelude:
  `Map.empty`, `fromList`, `toList`, `get`, `containsKey`, `insert`, `remove` and `count`, and `List.groupBy` gives
  one.
- **Literals.** `[k => v, ...]` is a map when every item is an entry. The parser makes it a call to the prelude's
  `__map` with the list of the entries as tuples, `__map([(k, v), ...])` (`MapSyntax` in `Nanyx.Parser/Ast.fs`), so the
  type checker and the backend see an ordinary call. Entries and elements in one literal are a parse error. `[]` is
  always the empty list; the empty map is `Map.empty`.
- **Keys need equality.** Every function that looks a key up requires `[Eq(k)]`, so a key type that holds a function
  is an error at the literal or the call.
- **Calling a map is a lookup.** `m(k)` is `Map.get(m, k)`, which gives `#some(v) | #notFound`. So is a call of a
  record's field that is a map, `machine.inventory(name)`. The type checker types the call as the call to `Map.get`
  (`inferMapLookup` in `Infer.fs`) and records it in `ReadAs`, and `ExpressionRewrites.writeAsRead` writes it as that
  call for the backend.
- **Loops.** `for m | key, value -> ...` walks a map's entries, each a tuple of its key and its value.
- **Representation.** A map holds the list of its entries, each key once, in the order the keys were first added. The
  prelude builds and reads it through two intrinsics, `__nanyx_map_of_entries` and `__nanyx_map_entries`, which the
  backend compiles to nothing: a `map(k, v)` is held as a `list((k, v))`. A lookup walks the list.
- **No `==` on maps yet.** Comparing the lists of entries would make two maps with the same entries, added in
  another order, unequal, so `==` on a map, or on anything holding one, is an error until
  [#618](https://github.com/nanyx-lang/nanyx/issues/618).

Known gaps: a literal whose keys are different tags, `[#red => 1, #green => 2]`, is a type error, because the entries
are a list literal of different tags ([#481](https://github.com/nanyx-lang/nanyx/issues/481)).
`examples/vendingMachine.nyx` type-checks its maps but does not compile for other reasons
([#619](https://github.com/nanyx-lang/nanyx/issues/619)).

# What #533 changes

The design in [Equality and well-known contexts](./equality-and-well-known-contexts#hashing) makes maps hash-based:
`Hash(a)` (`hash: a -> int`) is derived for every type `Eq` is, and a map or a set keyed by `k` requires
`[Eq(k) & Hash(k)]`. The list of entries becomes a persistent hash trie written in the prelude, behind the same two
intrinsics, so nothing outside the prelude changes but the requirement. `Set(a)` is a map to unit.

# Open questions

These are the choices the docs leave open. The code takes the marked default until they are answered.

1. **A key written twice in a literal.** `["a" => 1, "a" => 2]`.
   - *(Default)* The later value wins, as in `Map.fromList`, JavaScript, Python and F#.
   - An error, or a warning, when two keys are the same constant. Catches typos, but only for constant keys.
   - **Recommended:** the later value wins, plus a warning for two equal constant keys, added with the linters.
2. **The order of a map's entries.** What `Map.toList` and `for` give, and so what `==` and `hash` on maps see.
   - *(Default, until #533)* The order the keys were first added, which an association list gives for free.
   - The order of the keys' hashes. Deterministic for a given set of keys whatever order they were added in, so
     equal maps have one form, and `==` and `hash` on maps can be derived from structure (closing #618 with no code
     of its own). The order is meaningless to a reader, and changes if `hash` does.
   - Insertion order kept by a hash map (Python's `dict`). Predictable, but a persistent version needs a second
     index, and equal maps still differ in order.
   - **Recommended:** the order of the keys' hashes once #533 lands, documented as unspecified, with
     `Map.toList \List.sortBy(...)` for a chosen order.
3. **Is a map a function?** [Callable values](../tour/functions#callable-values) gives a map literal the type
   `k -> #some(v) | #notFound`. Today a map can be *called*, but it is not a function value: it cannot be passed
   where a function is expected, or annotated with a function type.
   - *(Default)* Not a function: write `{ k -> m(k) }` where one is needed.
   - A map is assignable to its lookup's function type: the type checker accepts a `map(k, v)` where a
     `k -> #some(v) | #notFound` is wanted, and the backend wraps it in a closure there.
   - **Recommended:** make a map assignable to its lookup's type, which keeps the tour's promise; until then the
     tour's example is written with `map(string, string)`.
4. **Builders.** [Collections](../tour/collections#syntax) has square-bracket literals call a target type's `apply`
   with a `ListBuilder` or `MapBuilder` context, so any type can have literals. Today a map literal is always a
   `map(k, v)`, and a list literal always a `list(a)`. **Recommended:** leave builders for the
   [type members](./type-members-and-reflection) work; nothing here gets in their way, since `__map` is one place to
   change.

Settled as the obvious choice:

- **Spelling.** The type is `map(k, v)` and the module `Map`, as with `list(a)` and `List`; the docs' `Map(k, v)` is
  the same type.
- **No empty literal.** `[]` stays the empty list; the empty map is `Map.empty`. A `[=>]` could be added if wanted.
