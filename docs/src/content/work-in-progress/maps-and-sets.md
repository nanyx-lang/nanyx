---
title: "Maps and sets"
description: "The map(k, v) and set(a) types, map literals, and what maps and sets still need"
order: 10
---

> **Status: implemented, keyed by `Eq` and `Hash`, but not yet hash-based.** `map(k, v)`, map literals, lookups and
> `list.groupBy` came with [#477](https://github.com/nanyx-lang/nanyx/issues/477); the `Hash` context, keys that
> require it, and `set(a)` with [#533](https://github.com/nanyx-lang/nanyx/issues/533). Both still hold a list, so a
> lookup takes time in proportion to the size. The [open questions](#open-questions) need the maintainer's answer; the
> defaults the code takes until then are marked.

# What is implemented

- **The types.** `map(k, v)` and `set(a)` are types the compiler knows, as it knows `list(a)`; the capitalised
  `Map(k, v)` and `Set(a)` are deprecated spellings of them, read with a warning for one release
  (`Nanyx.Compiler/Maps.fs`, `Sets.fs` and `DeprecatedTypeNames.fs`). The functions on them are attached to `map` and
  `set`, and documented on the [map](../stdlib/map) and [set](../stdlib/set) pages; `list.groupBy` gives a map.
- **Literals.** `[k => v, ...]` is a map when every item is an entry. The parser makes it a call to the prelude's
  `__map` with the list of the entries as tuples, `__map([(k, v), ...])` (`MapSyntax` in `Nanyx.Parser/Ast.fs`), so the
  type checker and the backend see an ordinary call. Entries and elements in one literal are a parse error. `[]` is
  always the empty list; the empty map is `map.empty`. There is no set literal: `set.fromList([1, 2])`.
- **Keys need `Eq` and `Hash`.** Every function that looks up a key or an element requires `[Eq(k) + Hash(k)]`, as
  [the equality design](./equality-and-well-known-contexts#hashing) agreed. So a key type that holds a function is an
  error at the literal or the call, and so is a nominal type that attaches `==` without `hash`. The hash is not used
  yet: lookups compare with `==`.
- **Calling a map is a lookup.** `m(k)` is `map.get(m, k)`, which gives `#some(v) | #notFound`. So is a call of a
  record's field that is a map, `machine.inventory(name)`. The type checker types the call as the call to `map.get`
  (`inferMapLookup` in `Infer.fs`) and records it in `ReadAs`, and `ExpressionRewrites.writeAsRead` writes it as that
  call for the backend.
- **Loops.** `for m | key, value -> ...` walks a map's entries, each a tuple of its key and its value, and
  `for s | x -> ...` a set's elements.
- **Representation.** A map holds the list of its entries, each key once, in the order the keys were first added, and
  a set the list of its elements. The prelude builds and reads them through four intrinsics,
  `__nanyx_map_of_entries`, `__nanyx_map_entries`, `__nanyx_set_of_elements` and `__nanyx_set_elements`, which the
  backend compiles to nothing: a `map(k, v)` is held as a `list((k, v))`, and a `set(a)` as a `list(a)`.
- **No `==`, order or hash of maps and sets yet.** Comparing the lists would make two maps or sets with the same
  contents, added in another order, unequal, so `==`, `<` or `Hash` of one, or of anything holding one, is an error
  until [#618](https://github.com/nanyx-lang/nanyx/issues/618).

Known gaps:

- An unannotated chain of inserts whose keys are tags of different names is a type error: the keys are not joined.
  A literal, `[#red => 1, #green => 2]`, is a `map(#red | #green, int)`, as a list literal's items are joined, and
  writing the key type, `map(#red | #green, int)` or a function typed with it, works.
- `examples/vendingMachine.nyx` type-checks its maps but does not compile for other reasons
  ([#619](https://github.com/nanyx-lang/nanyx/issues/619)).
- A signature joins its contexts with `+`, while the docs write `&`
  ([#645](https://github.com/nanyx-lang/nanyx/issues/645)).

# What is left: hash-based maps

The list of entries is to become a persistent hash trie written in the prelude, behind the same intrinsics, so
nothing outside the prelude changes. A trie is a recursive type, `#leaf(...) | #node(Trie(k, v), Trie(k, v))`, and a
type alias cannot refer to itself yet ([#246](https://github.com/nanyx-lang/nanyx/issues/246)), so this waits on that,
and on [open question 2](#open-questions), which decides the order a hash-based map gives its entries in.
# Open questions

These are the choices the docs leave open. The code takes the marked default until they are answered.

1. **A key written twice in a literal.** `["a" => 1, "a" => 2]`.
   - *(Default)* The later value wins, as in `map.fromList`, JavaScript, Python and F#.
   - An error, or a warning, when two keys are the same constant. Catches typos, but only for constant keys.
   - **Recommended:** the later value wins, plus a warning for two equal constant keys, added with the linters.
2. **The order of a map's entries.** What `map.toList` and `for` give, and so what `==` and `hash` on maps see.
   - *(Default, while maps hold a list)* The order the keys were first added, which a list gives for free.
   - The order of the keys' hashes. Deterministic for a given set of keys whatever order they were added in, so
     equal maps have one form, and `==` and `hash` on maps can be derived from structure (closing #618 with no code
     of its own). The order is meaningless to a reader, and changes if `hash` does.
   - Insertion order kept by a hash map (Python's `dict`). Predictable, but a persistent version needs a second
     index, and equal maps still differ in order.
   - **Recommended:** the order of the keys' hashes once maps are hash tries, documented as unspecified, with
     `map.toList \list.sortBy(...)` for a chosen order.
3. **Is a map a function?** [Callable values](../tour/functions#callable-values) gives a map literal the type
   `k -> #some(v) | #notFound`. Today a map can be *called*, but it is not a function value: it cannot be passed
   where a function is expected, or annotated with a function type.
   - *(Default)* Not a function: write `{ k -> m(k) }` where one is needed.
   - A map is assignable to its lookup's function type: the type checker accepts a `map(k, v)` where a
     `k -> #some(v) | #notFound` is wanted, and the backend wraps it in a closure there.
   - **Recommended:** make a map assignable to its lookup's type, which keeps the tour's promise; until then the
     tour's example is written with `map(string, string)`.
4. **Builders.** [Collections](../tour/collections#syntax) has square-bracket literals call a target type's `apply`
   with a `ListBuilder` or `MapBuilder` context, so any type can have literals, and
   `def unique: set(int) = [1, 2, 2, 3]` would be a set; the tour marks this as planned. Today a map literal is always
   a `map(k, v)`, and a list literal always a `list(a)`, so a set is made with `set.fromList`. **Recommended:** leave builders for the
   [type members](./type-members-and-reflection) work; nothing here gets in their way, since `__map` is one place to
   change.

Settled as the obvious choice:

- **Spelling.** The type is `map(k, v)`, and its functions are attached to it by that name, `map.get`, as `list.map`
  is to `list(a)` (#653).
- **No empty literal.** `[]` stays the empty list; the empty map is `map.empty`. A `[=>]` could be added if wanted.
