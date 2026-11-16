---
title: "Mutation"
description: "How local cells are built, and the rest of the plan for mutation under Memory"
order: 2
---

Local cells, cell parameters, `while`, and the mutable values `MRef`, `MArray`, `MSlice`, `MStack` and `MList` are
built ([#756](https://github.com/nanyx-lang/nanyx/issues/756), [#1072](https://github.com/nanyx-lang/nanyx/issues/1072),
[#957](https://github.com/nanyx-lang/nanyx/issues/957), [#1491](https://github.com/nanyx-lang/nanyx/issues/1491) to
[#1493](https://github.com/nanyx-lang/nanyx/issues/1493), [#1545](https://github.com/nanyx-lang/nanyx/issues/1545)).
[Memory](../advanced/memory), [Loops](../tour/loops) and [Collections](../tour/collections#arrays-and-slices) describe
them. This page keeps how they are built, and `mut` record fields, which are still planned.

# How it works

- **Type checking** (`compiler/Nanyx.Compiler/MemoryCells.fs`). The block of a call to the prelude's `memory` is
  checked with a name in the environment no code can write, bound to the block's scope, which is what a `mut`
  declaration looks for. A def whose value is a call to `__mut` is a cell: its symbol is in `InferState.Cells`, with the
  scope of its block, and it is not generalised. `:=` checks that its target resolves to a cell, and adds an
  assignability constraint reported at the `:=`, whose mismatches get their own code. Each read or change of a cell
  makes the lambda around it require `Memory` of the cell's scope (`MemoryCells.recordUse`), so a lambda over a cell
  says so in its type. `Memory` is scoped, so the block's result must not require the block's own `Memory`
  (`ScopedCalls`, NYX0233; see [Context escape](./context-escape#scoped-contexts)).
- **Backend** (`ExpressionRewrites.writeCells`, then `CoreIrLower.fs`). Once inference has resolved names, each read of
  a cell becomes a call to a `CellSyntax` built-in, and a cell some lambda other than a `for` body captures becomes a
  box: a record of one field, made with `box`, read with `unbox` and written with `setBox`. The core IR has two nodes
  for writes, `Assign` for a local and `SetField` for a box, each dropping the value it replaces. A read copies the
  value into a local of its own, so that a later `:=` cannot change what a read gave. A cell's local or box has the
  cell's type, which the checker records as the type of its `mut` (`MemoryCells.typeMut`): it can be a union wider
  than the first value's, `mut found: #some(int) | #none = #none`, and a cell typed as its first value's `#none` was
  not counted as a value on the heap, so a `match` on it freed what it held.
- **Cell parameters** (`CellPassingSyntax`). The parser reads `mut cell` as a parameter annotated `mut _` with no source
  of its own, so the formatter writes it back, and `mut x` before a `,` or `)` as a call to `__mut_arg`. `mut T` is
  `TyApply("mut", [T; scope])` (`FunctionContexts.cellType`): the scope is the signature's `Memory`'s where an
  annotation writes it, and a parameter's own otherwise, so a passed cell's block reaches whatever requires the
  parameter and NYX0233 still keeps it in its block. `MemoryCells.bindParameter` makes the parameter a cell of that
  scope, `MemoryCells.inferArgument` types `mut x` (NYX0234 when `x` is not a cell), and
  `MemoryCells.ensureCellTypesPlaced` reports a `mut T` anywhere but a function's parameter (NYX0235). In the backend a
  cell that is passed is a box, a cell parameter is the box it is given, and `mut x` passes the box.
- **`while`** ([#957](https://github.com/nanyx-lang/nanyx/issues/957)). The parser makes `while condition -> body` a
  call to `__while` (`WhileSyntax`). Neither part is a lambda, so the checker (`Loops.inferWhile`) types them where
  the loop is written, and the core IR has a `While` node that evaluates both again each time round, borrowing what
  they use from outside.
- **A `mut` parameter implies `Memory`** ([#1075](https://github.com/nanyx-lang/nanyx/issues/1075)). `Memory` is
  nominal and has no members, so the member-based inference of a def's contexts never sees it: a top-level def that
  writes no type and whose own lambda takes a cell has it added by name to `InferredContexts`, as an ambient context
  is (`MemoryCells.parameterContexts`); any other lambda's type carries it. The unifier treats every function type that
  takes a cell as requiring `Memory` of the cell's scope (`FunctionContexts.withCellMemory`), so `mut int -> ()` is
  `[Memory] mut int -> ()` written or not. A def that writes contexts without `Memory` and takes a cell is NYX0211
  (`MemoryCells.ensureMemoryWritten`). A call is given the `Memory` of the cell it passes, which the call checks
  (`ScopedCalls`), so the pass that carries ambient contexts from a def to its callers (`DeferredContextUses`, used
  where a host module is imported) neither passes `Memory` on nor reports it: a call in a `memory` block is not a
  requirement of the def around the block ([#1473](https://github.com/nanyx-lang/nanyx/issues/1473)). A call that
  requires `Memory` outside any lambda is checked against the def around it, as any scoped context is (see
  [Context escape](context-escape.md), `ScopedCalls.unsupplied`): NYX0211 where the def writes its type without
  `Memory` and takes no cell, and an error in a top-level value or a script's statements, so an `MRef` can't be made
  outside a block. A top-level def that writes no type is inferred to require it, and its type carries it
  ([#1561](https://github.com/nanyx-lang/nanyx/issues/1561)).

# Mutable values

Decided by the maintainer: mutable values carry their `memory` block's scope in their types, as a cell parameter's
`mut T` does. The scope is never written or shown, each operation requires `Memory` of it, and NYX0233 keeps the
value, and anything holding it, inside its block. The types are declared with the feature any user has for a scoped
context of their own, `type MRef(a) = [Memory] protected (...)`
([Types in a scope](../advanced/contexts#types-in-a-scope), done in
[#1521](https://github.com/nanyx-lang/nanyx/issues/1521)), not by a special case in the compiler.

1. **`Array(a)`** ([#1490](https://github.com/nanyx-lang/nanyx/issues/1490)): done. An immutable, fixed-length
   sequence with constant-time indexing, in [`nanyx/array`](../stdlib/array). It needs no `Memory`. In the backend
   it's an `IrArray`, a block of its length and then its elements laid out flat (`CoreIrArrays`, and see
   [Memory management](./memory-management#drop-functions)); four intrinsics make and read one. An array is callable,
   `xs(i)` being `array.at(xs, i)`, read as a map's call is (`Infer.inferLookup`,
   [#1516](https://github.com/nanyx-lang/nanyx/issues/1516)); `array.at` is in the prelude for it.
2. **`MRef(a)`, a cell as a value** ([#1491](https://github.com/nanyx-lang/nanyx/issues/1491)): done, in
   [`nanyx/mref`](../stdlib/mref), and described on [Memory](../advanced/memory#references). It is declared
   `type MRef(a) = [Memory] private box(a)`. `box(a)` is a built-in type (`Boxes.fs`) that only the standard library
   can make, read or change, through three intrinsics, `__nanyx_box_new`, `__nanyx_box_get` and `__nanyx_box_set`. In
   the backend a box is the box a captured cell is, a record of one field (`CoreIrLowerTypes`): `box_new` is a
   `MakeRecord`, `box_get` a `Field` copied into a local of its own, as a read of a cell is, and `box_set` a `SetField`,
   which gives up the value it replaces; its drop is the record's. The optimiser folds no record's field (only a
   closure's), so a box changed in place is safe there. A box has no `Eq`, `Ord` or `Hash`
   (`WellKnownContexts`, "mutable references have no equality"), and an `MRef` no `ToString`, since writing what it
   holds would read it without `Memory`. A def's type is not generalised over the variables in a box its value holds,
   through the types its nominal types stand for, though it is over those of a box a function it holds makes
   (`Boxes.heldVariables`, used by `Infer.generalizeDef`): this is the value restriction, for boxes only.
3. **`MArray(a)`** ([#1492](https://github.com/nanyx-lang/nanyx/issues/1492)): done, in
   [`nanyx/marray`](../stdlib/marray). Fixed length, each slot changed in place: it behaves as an `Array(MRef(a))`
   whose cells are all distinct, but is laid out flat, as an `Array` is. It is declared
   `type MArray(a) = [Memory] private marray(a)`. `marray(a)` is a built-in type of its own (`MutableArrays` in
   `Arrays.fs`), not an `array`, so that it has no `Eq`, `Ord`, `Hash` or `ToString`, and a def's type is not
   generalised over its elements (`Boxes.heldVariables`). The backend holds it as an `IrArray`: four of its five
   intrinsics are the array's (`Intrinsics.asArrayIntrinsic`, applied where they are lowered), and `__nanyx_marray_set`
   stores an element in place, giving up the one it replaces as `SetField` does, and takes the value over
   (`Helpers.operandUse`). Converting between `Array` and `MArray` copies, through a list, so an `Array` stays
   immutable. `set` and `swap` give `#ok | #outOfRange`, never a trap.
4. **`MStack(a)` and `MList(a)`** ([#1493](https://github.com/nanyx-lang/nanyx/issues/1493)): done, in
   [`nanyx/mstack`](../stdlib/mstack) and [`nanyx/mlist`](../stdlib/mlist), written in Nanyx over `MRef` as private
   types, so they needed nothing new in the compiler. Each declares `[Memory]`, as any type whose representation holds
   a scoped value must (NYX0256, [#1604](https://github.com/nanyx-lang/nanyx/issues/1604)):
   `type MStack(a) = [Memory] private MRef(list(a))`, so the `MRef` it holds is of the stack's own scope.
   `MStack` is an `MRef(list(a))`, the top first. `MList` is an `MRef` of its elements in order, the last of their
   cells, and their count ([#1671](https://github.com/nanyx-lang/nanyx/issues/1671)): `add` makes a cell and changes
   the last cell's tail to it, with the intrinsic `__nanyx_list_set_tail`, so it is constant-time, and `at` and `set`
   walk to the element; `set` gives `#ok | #outOfRange`. Changing a cell is safe because nothing outside the `MList`
   sees its cells until `toList` gives them out. `toList` is functional but in place
   ([Principles](../foundations/principles#functional-but-in-place)): it asks `__nanyx_is_exclusive` whether it holds
   the only reference to the `MList`'s box, and if so hands over the cells, in constant time, since nothing can add to
   them again; otherwise the program uses the `MList` afterwards, so it copies them. A copy is a cost, never a change
   in meaning. `list.select` builds its list the same way, front to back in a `memory` block, with nothing to check:
   its cells are never seen until it returns them. `MList` can move onto a growable `MArray` later without changing
   its interface.
   `+=` appends too ([#1564](https://github.com/nanyx-lang/nanyx/issues/1564), decided by the maintainer): it is a
   spare operator, the loosest, grouping none, which means only what is attached to its left operand's type, as `^`
   and `<>` do. `MList` attaches ``def MList.`+=`: [Memory] MList(a), a -> () = MList.add``. It is not sugar for
   `:=`: nothing reads it as assignment, so `x += 1` on a cell is an error that says to write `x := x + ...`
   (`OperatorCalls.undefinedSpare`). An operator whose attached definition requires plain contexts, as this one
   requires `Memory`, requires them where it is written, as a call to the definition would
   (`Contexts.requireOperatorContexts`).
5. **Slices** ([#1545](https://github.com/nanyx-lang/nanyx/issues/1545)): done, views that share storage instead of
   copying, each a type of its own written in Nanyx, so a small slice can't silently keep a large parent alive, and
   `Array` stays a plain flat block. `Slice(a)` in [`nanyx/slice`](../stdlib/slice) is a private record of the
   `Array`, a start and a length: it holds a counted reference to the array, so it needs no lifetime rules.
   `MSlice(a)` in [`nanyx/mslice`](../stdlib/mslice) is the same over an `MArray`, declared
   `type MSlice(a) = [Memory] private (...)`, so NYX0233 keeps it in its block; `set`, `swap`, `fill`, `reverse`
   and `sort` (an insertion sort) change the array in place. `StringSlice` in [`nanyx/text`](../stdlib/text) views
   a run of a string's bytes, for parsers and lexers. Bounds clamp, as Python's do, and reading or setting one
   element past the end is `#none` or `#outOfRange`. `toArray` and `toString` copy. Still open: spreading a slice into
   a literal, `Array[...s]`, which waits for the `Iterable` context that spreading a set does
   ([Collection literals](./collection-literals)).

# Planned

- **`mut` record fields.** The parser reads `mut` before a field name in a record type; nothing checks or compiles it
  yet.
