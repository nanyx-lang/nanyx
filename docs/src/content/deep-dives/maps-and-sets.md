---
title: "Maps and sets"
description: "How map(k, v) and set(a) are built"
order: 8
---

What maps and sets do is described on [maps and sets](../stdlib/prelude-maps) and in
[Collections](../tour/collections). This page says how the compiler builds them. What is still open is in issues: tag
keys of different names in a chain of inserts ([#2757](https://github.com/nanyx-lang/nanyx/issues/2757)), a linter
rule for a key written twice in a literal ([#2760](https://github.com/nanyx-lang/nanyx/issues/2760)), and whether a
map can be passed where its lookup's function type is wanted ([#2763](https://github.com/nanyx-lang/nanyx/issues/2763)).

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
- **The store** is a hash trie: `#empty`, the map with no keys, and `#node(leafBits, leaves, nodeBits, nodes)`, the
  keys whose hashes agree on the bits the nodes above read, split by the next five bits of the hash, from its low end.
  `leafBits` has a bit set for each five-bit value whose keys the array `leaves` holds as a *bucket*, and `nodeBits` for
  each whose keys a node of the array `nodes` holds, each array in the order of the values. A bucket is a list of the
  entries, each a key and its value, of the keys whose hash is the same: one, or, when hashes collide, more. It doesn't
  keep the hash, so an insert that meets a bucket of other keys hashes one of them again to tell a collision from two
  keys to split. A set is the store of a map from its elements to `()`. A lookup, an insert or a remove reads one node a level,
  and a level takes five bits of a 64-bit hash, so it takes time that grows with the log of the size (`findIn`,
  `insertedIn` and `removedFrom` in `prelude-maps.nyx`). An insert makes only the nodes on the key's path new, and
  shares the rest.
- **One trie for one set of keys.** The root is a node, or `#empty`. A node below it has two buckets or nodes or more, or
  one node, as inserting two keys whose hashes agree on more bits makes; a node left with one bucket alone by a remove,
  or by `map.filter`, is that bucket in the node above instead. The
  keys in a bucket are in the compiler's own order of their structure, which the backend has for every type
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
  prelude's `map.toList` and `set.toList` (`Maps.toList` and `Sets.toList`), which linking always keeps, as `dbg`
  does (`CoreIrLowerDbg.fs`), but for a `for` loop, which walks the store's trie itself and hands the body the entries
  its buckets hold, borrowed, making nothing ([#2661](https://github.com/nanyx-lang/nanyx/issues/2661)): the
  backend's `OverStore` loop source (`StoreWalk` in `CoreIr.fs`, its code in `CoreIrEmitStoreWalk.fs`) goes into
  the trie's nodes a level at a time. In each node it takes the run of buckets before the next node straight from
  `leaves`, by index, then goes into that node, keeping its five-bit value at each level in five bits of one `int`, and
  goes back up by going down again from the root along it. A bucket of one entry costs three loads, its slot, the
  entry and the end of the list, where a list's walk reads two, and the walk takes about 1.3 to 1.9 times as long as
  a list's ([#2671](https://github.com/nanyx-lang/nanyx/issues/2671)). A map
  walked in step with a sequence is walked as the list `toList` gives (`CoreIrLowerLoops.forEachPulling`). A `for`
  loop in a builder's block whose body uses a keyword is run by the builder's `for` member, which takes a list, so a
  map or a set there is copied to the list `toList` gives (`WorkflowMembers.loopOverStore`,
  [#2672](https://github.com/nanyx-lang/nanyx/issues/2672)), unlike a plain loop's. A type's
  description boxes a map's keys and values in place, and unboxes them again, through the prelude's
  `__mapStoreConverted` (`Maps.convertedStore`), which keeps the store's shape and hashes: nothing hashes or compares
  a boxed key, and a lookup by one is refused when the program compiles, as `==` on a box is.
- **`==` and `hash` compare contents, in any order** ([#618](https://github.com/nanyx-lang/nanyx/issues/618)). Equal
  maps hold the same trie, so the compiler's own `==` and hash, which read the store by its structure, compare and
  hash them right, as they do a list: a map or a set, or anything holding one, is compared and hashed by them, and can
  be a key or a set's element. Where a key's or a value's type has an `==` or a `hash` of its own, two equal maps can
  hold different keys or values that it finds equal, so the compiler derives the map's from its parts instead (`MapShape`
  and `SetShape` in `StructuralInstances.fs`), as for a list whose elements have their own. The derived `==` calls the
  prelude's `__mapEqualsBy` or `__setEqualsBy`, which takes time in proportion to the product of the sizes, and the
  derived `hash` calls `__mapHashBy` or `__setHashBy`, which combine the entries' hashes in no order. A type variable
  nothing solves in a derived comparison, as the element of an empty list's type, holds no value, so it is compared as
  `()`, which the backend makes it (`ContextInstances.derivedComparison`,
  [#2522](https://github.com/nanyx-lang/nanyx/issues/2522)). A map or a set has no order: `<` on one is an error.
- **A call that needs `Eq` of a type with variables in it**, such as `list.distinct` on a `list(set(a))` inside a
  function that requires `[Eq(a)]`, is passed the derived `==` as a closure over the `Eq(a)` the function was given
  ([#630](https://github.com/nanyx-lang/nanyx/issues/630)).
