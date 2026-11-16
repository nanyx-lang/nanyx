---
title: "Mutation"
description: "How local cells are built, and the rest of the plan for mutation under Memory"
order: 2
---

Local cells, cell parameters and `while` are built ([#756](https://github.com/nanyx-lang/nanyx/issues/756),
[#1072](https://github.com/nanyx-lang/nanyx/issues/1072), [#957](https://github.com/nanyx-lang/nanyx/issues/957)).
[Memory](../advanced/memory) and [Loops](../tour/loops) describe them. This page keeps how they are built and what
is planned.

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
  value into a local of its own, so that a later `:=` cannot change what a read gave.
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
  (`MemoryCells.ensureMemoryWritten`). Not yet: a def that writes no type and only calls a function that requires
  `Memory`, outside any `memory` block, is not inferred to require it, since nothing tracks `Memory` by name there.

# Planned

Decided by the maintainer: mutable values carry their `memory` block's scope in their types, as a cell parameter's
`mut T` does, and need nothing more general. The scope is never written or shown, each operation requires `Memory` of
it, and NYX0233 keeps the value, and anything holding it, inside its block. This is the "Scope and escape" part of
[Context-qualified types](./context-qualified-types), for `Memory` only; a context on any other type waits for a use
that needs one, and for [#1059](https://github.com/nanyx-lang/nanyx/issues/1059).

1. **`Array(a)`** ([#1490](https://github.com/nanyx-lang/nanyx/issues/1490)): an immutable, fixed-length sequence with
   constant-time indexing, in `nanyx/array`. Lists are cons cells, so this is a new kind of heap block. It needs no
   `Memory`.
2. **`MRef(a)`, a cell as a value** ([#1491](https://github.com/nanyx-lang/nanyx/issues/1491)): held in a record, a list
   or another cell, passed without `mut`, or returned from a function, all inside its block. In the backend it's the
   box a captured cell already is. After [#1446](https://github.com/nanyx-lang/nanyx/issues/1446), which changes how a
   block's `Memory` reaches the types in it.
3. **`MArray(a)`** ([#1492](https://github.com/nanyx-lang/nanyx/issues/1492)): fixed length, each slot changed in place.
   It behaves as an `Array(MRef(a))` whose cells are all distinct, but is laid out flat, as an `Array` is. Converting
   between `Array` and `MArray` copies, so an `Array` stays immutable.
4. **`MStack(a)` and `MList(a)`** ([#1493](https://github.com/nanyx-lang/nanyx/issues/1493)): written in Nanyx over
   `MRef`, as protected types, so they need nothing new in the compiler. `MList` has `+=` attached to append. It can
   move onto a growable `MArray` later without changing its interface.

- **`mut` record fields.** The parser reads `mut` before a field name in a record type; nothing checks or compiles it
  yet.
