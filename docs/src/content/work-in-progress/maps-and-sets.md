---
title: "Maps and sets"
description: "What maps and sets still need"
order: 10
---

What maps and sets do is described on [maps and sets](../stdlib/prelude-maps) and in
[Collections](../tour/collections), and how they are built in [Maps and sets](../deep-dives/maps-and-sets) in the Deep
dives section. Their keys require `Eq` and `Hash`, but both hold a list, so a lookup takes time in proportion to the
size. This page keeps their known gaps, the move to hash-based maps, and the [open questions](#open-questions), which
need the maintainer's answer; the defaults the code takes until then are marked.

# Known gaps

- **Comparing maps in a local function.** A local function that compares values generic in their type is passed the
  compiler's own `==`, which cannot compare a map, so it is an error when the type holds a map or a set.
- **No order or hash of maps and sets.** `<` on a map or a set, or on anything holding one, is an error, and so is a
  map or a set as a key or a set's element: hashing the list underneath would give equal maps different hashes.
- **Tag keys of different names.** An unannotated chain of inserts whose keys are tags of different names is a type
  error: the keys are not joined. A literal, `[#red => 1, #green => 2]`, is a `map(#red | #green, int)`, as a list
  literal's items are joined, and writing the key type works.

# What is left: hash-based maps

The list of entries is to become a persistent hash trie written in the prelude
([#2642](https://github.com/nanyx-lang/nanyx/issues/2642)). The list already holds its entries in the order the trie
will walk them, the order of their keys' hashes, which is unspecified
([Representation](../deep-dives/maps-and-sets#how-it-works)), so the trie will not change it.

# Open questions

These are the choices the docs leave open. The code takes the marked default until they are answered.

1. **A key written twice in a literal.** `["a" => 1, "a" => 2]`.
   - *(Default)* The later value wins, as in `map.fromList`, JavaScript, Python and F#.
   - An error, or a warning, when two keys are the same constant. Catches typos, but only for constant keys.
   - **Recommended:** the later value wins, plus a warning for two equal constant keys, added with the linters.
2. **Is a map a function?** A map can be *called*, but it is not a function value: it cannot be passed where a
   function is expected, or annotated with a function type.
   - *(Default)* Not a function: write `{ k -> m(k) }` where one is needed.
   - A map is assignable to its lookup's function type: the type checker accepts a `map(k, v)` where a
     `k -> #some(v) | #notFound` is wanted, and the backend wraps it in a closure there.
   - **Recommended:** make a map assignable to its lookup's type.
