---
title: "Collection literals"
description: "How a square-bracket literal builds any type with a fromArray, and what is still open"
order: 11
---

Done in [#1543](https://github.com/nanyx-lang/nanyx/issues/1543), and described in the tour's
[Collections](../tour/collections#syntax): a square-bracket literal builds any type with an attached `fromArray`,
chosen by the expected type (`def a: Array(int) = [1, 2]`) or by the type's name before the brackets
(`Array[1, 2]`). With neither, a literal is a `list(a)`, or a `map(k, v)` of `k => v` entries, as before.

# How it works

- **The constructor form.** The parser writes `T[...]` as the call `T.fromArray(__arrayOf(items))`
  (`CollectionSyntax.build`, `Expressions.collectionConstruct`), so the rest of the compiler sees an ordinary call:
  attached-name lookup, `BuiltinTypeNames` (`Array[...]` is `array.fromArray`), and the contexts the `fromArray`
  requires. `items` is the list of the items, each run of elements a list literal, each spread a call to the built-in
  `__spreadItems`, joined with `++`; map entries are tuples. A `T` with no `fromArray` is NYX0257 at the name. The
  formatter prints the call back as `T[...]` (`CollectionSyntax.Construct`).
- **The expected type.** `CollectionLiterals.targeted` reads a literal written where a def's declared type, or the
  parameter of the function it's passed to, is a nominal type, a set, an array, or a map (for plain items or `[]`), as
  the call of that type's `fromArray`, found as a pipe finds an attached definition (`tryResolveAttachedFunction`). The
  literal keeps its node, and the call is recorded in `ReadAs`, which `ExpressionRewrites.writeAsRead` writes into the
  syntax the backend reads, with the items as they are rewritten. An expected list keeps a list literal a list, and an
  expected map keeps a map literal a map. Any other expected type with no `fromArray` is NYX0257 at the literal. A
  literal that is the last statement of a typed def's block, as one written on the lines under the `=` is, takes the
  def's declared type too: `inferDefValue` records the type in `ExpectedBlockResults` by the statement's node, and
  `inferBlock` reads it there ([#1776](https://github.com/nanyx-lang/nanyx/issues/1776)).
- **Spreads.** `__spreadItems(x)` is read as the prelude's `__spreadArray(x)` for an array and `__spreadList(x)` for a
  list (`CollectionLiterals.inferSpread`); anything else is NYX0258.
- **The standard library.** `fromArray` is attached to `list`, `set`, `map` and `Array` in the prelude, and to `MStack`,
  `MList`, `MArray`, `Stack`, `Queue`, `Trie` and `StringTrie` in their modules, written in Nanyx. `__arrayOf` gives the
  type of the array the items make, and the spreads' functions their types.
- **One flat allocation** ([#1641](https://github.com/nanyx-lang/nanyx/issues/1641)). The backend never builds the
  list: it lowers `__arrayOf` of the items in one block (`CoreIrLowerArrayLiterals`). It reads the items back into their
  parts, each run's items and each spread, makes the array at the number of items plus each spread's length with
  `__nanyx_array_new`, writes each item with `__nanyx_array_write`, and copies each spread array's elements in, each
  with a reference of its own (`__nanyx_array_copy_into`, written out where it is used). A spread list is made an array
  first, one block and no cells. So `Array[1, 2, 3]` is one block, and `Array[...xs, 4]` one beside `xs`.

# Still open

- **A `FromArray` context.** The issue suggested reading `fromArray` through a prelude context synthesised from it, as
  `ToString` is from `toString`, so that generic code could build literals of a type variable. Literals call the
  attached `fromArray` directly instead, and a literal expected as a type variable stays a list.
- **Where the expected type comes from.** Only a def's written type (on its line, or ending its block) and a call's
  parameter: a lambda's result, a
  record field's type and a pattern's don't make a literal another type yet. A literal that is only a spread, `[...xs]`,
  is parsed as `xs`, so it is never read as another type.
- **Spreading a set, a `seq` or a slice** waits for an `Iterable` context ([Lazy sequences](./lazy-sequences)).
