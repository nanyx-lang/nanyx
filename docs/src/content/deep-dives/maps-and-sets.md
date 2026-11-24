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
- **Keys need `Eq` and `Hash`**, as [Hash(a)](../advanced/contexts#hasha) describes: the hash finds a key's place in
  the trie, and `==` tells apart keys whose hashes are the same.
- **Calling a map is a lookup.** The type checker types `m(k)` as the call to `map.get` (`inferLookup` in
  `Infer.fs`, which reads a call of an array as `array.at` the same way) and records it in `ReadAs`, and `ExpressionRewrites.writeAsRead` writes it as that call for the backend.
  A call of a field that holds a function, `statement.setInt(position, value)`, is read the same way, as a block that
  keeps the field's value in a local and calls it (`FieldCalls`, [#1513](https://github.com/nanyx-lang/nanyx/issues/1513)).
- **Representation.** A map is held as its *store*, a `__mapStore(k, v)`, and a set as a `__setStore(a)`. These are
  types only the prelude writes, and their `__` keeps them out of completion, hovers and the standard library's pages;
  a map's type is always shown as `map(k, v)`. The shape of a store is defined once, by `Maps.storeOf` and
  `Sets.storeOf` in `Nanyx.Compiler`, and every algorithm that builds or reads one is in the prelude,
  `stdlib/nanyx/prelude-maps.nyx`: whoever changes one changes the other. The prelude turns a map into its store and
  back through four intrinsics, `__nanyx_map_of_store`, `__nanyx_map_store`, `__nanyx_set_of_store` and
  `__nanyx_set_store`, which the backend compiles to nothing, and the backend lays a map out as its store.
- **The store** is a hash trie: `#empty`, the map with no keys; `#leaf(h, key, value)`, one key with its hash;
  `#collision(h, entries)`, keys whose hashes are all `h`; and `#node(bits, children)`, the keys whose hashes agree on
  the bits the nodes above read, split by the next five bits of the hash, from its low end, with a bit of `bits` set for
  each five-bit value some key has and a trie for each in the array `children`, in the order of the values. A set is the
  store of a map from its elements to `()`. A lookup, an insert or a remove reads one node a level, and a level takes
  five bits of a 64-bit hash, so it takes time that grows with the log of the size (`findIn`, `insertedIn` and
  `removedFrom` in `prelude-maps.nyx`). An insert makes only the nodes on the key's path new, and shares the rest.
- **One trie for one set of keys.** A node has two children or more, or one that is a node, as inserting two keys whose
  hashes agree on more bits makes; a node left with one leaf or collision by a remove, or by `map.filter`, is that child
  instead. Keys in a collision are in the compiler's own order of their structure, which the backend has for every type
  a key can be (`CoreIrOrdering.fs`): tags by their numbers and then their payloads, and a recursive type as what it is
  made of, though neither has an `Ord`, so `<` on them is still an error. So the same keys make the same trie however
  they were added and removed, and its walk gives them in one order: by the lowest five bits of their hashes, then the
  five above those, and so on. That order is not one a program can rely on, and the docs call it unspecified. A tag's
  number, and so its hash too, is the program's own: the order is the same throughout a run, but not from one program to
  another, and nothing may rely on it beyond a run.
- **A recursive type can hold a map of itself**, as `type Json = ... | #object(map(string, Json))` does: the map's
  store is a recursive type inside the other, and the backend's `IrSelf` says which of the recursive types around it
  it stands for (`CoreIr.fs`).
- **The compiler reads a map only through the prelude.** It reads a map's entries or a set's elements through the
  prelude's `map.toList` and `set.toList` (`Maps.toList` and `Sets.toList`), which linking always keeps: a `for` loop
  over a map walks the list `toList` gives (`Loops.fs`), and so does `dbg` (`CoreIrLowerDbg.fs`). A type's
  description boxes a map's keys and values in place, and unboxes them again, through the prelude's
  `__mapStoreConverted` (`Maps.convertedStore`), which keeps the store's shape and hashes: nothing hashes or compares
  a boxed key, and a lookup by one is refused when the program compiles, as `==` on a box is.
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
