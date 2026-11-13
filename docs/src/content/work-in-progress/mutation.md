---
title: "Mutation"
description: "Local cells today, and the rest of the plan for mutation under $Memory"
order: 2
---

The design of mutation is on [Memory](../advanced/memory). Its first step, local cells, is done
([#756](https://github.com/nanyx-lang/nanyx/issues/756)); this page is what is left.

# What works

- The prelude declares the nominal context `$Memory` and `memory`, which runs a block with it.
- `mut x = e` declares a cell in a `memory` block, `:=` changes one, and a cell's name reads it. Cells are not values:
  nothing can hold one. `mut` declares one name and is not an expression ([#1071](https://github.com/nanyx-lang/nanyx/issues/1071)):
  the parser reads `mut x = e` as a def whose value is a call to `__mut` (`MutSyntax`), as `rec` does with `__rec`,
  and `mut` anywhere else, `def x = mut e` included, is a parse error that says to write `mut x = e`.
- A lambda in the block may use the block's cells, and its type then requires `$Memory`: `{ word -> count := count + 1 }`
  is `[$Memory] string -> ()`. A block whose result is, or holds, a lambda that uses one is an error.
- A cell no lambda captures is a WebAssembly local; a captured one is a reference-counted box the lambdas share.
- `while condition -> body` runs its body for as long as its condition, which may read cells, is true
  ([#957](https://github.com/nanyx-lang/nanyx/issues/957)). The parser makes it a call to `__while` (`WhileSyntax`);
  neither part is a lambda, so the checker (`Loops.inferWhile`) types them where the loop is written, and the core IR
  has a `While` node that evaluates both again each time round, borrowing what they use from outside.

# How it works

- **Type checking** (`compiler/Nanyx.Compiler/MemoryCells.fs`). The block of a call to the prelude's `memory` is
  checked with a name in the environment no code can write, bound to the block's scope, which is what a `mut`
  declaration looks for. A def whose value is a call to `__mut` is a cell: its symbol is in `InferState.Cells`, with the
  scope of its block, and it is not generalised. `:=` checks that its target resolves to a cell, and adds an
  assignability constraint reported at the `:=`, whose mismatches get their own code. Each read or change of a cell
  makes the lambda around it require `$Memory` of the cell's scope (`MemoryCells.recordUse`), so a lambda over a cell
  says so in its type. `$Memory` is scoped, so the block's result must not require the block's own `$Memory`
  (`ScopedCalls`, NYX0233; see [Context escape](./context-escape#scoped-contexts)).
- **Backend** (`ExpressionRewrites.writeCells`, then `CoreIrLower.fs`). Once inference has resolved names, each read of
  a cell becomes a call to a `CellSyntax` built-in, and a cell some lambda other than a `for` body captures becomes a
  box: a record of one field, made with `box`, read with `unbox` and written with `setBox`. The core IR has two nodes
  for writes, `Assign` for a local and `SetField` for a box, each dropping the value it replaces. A read copies the
  value into a local of its own, so that a later `:=` cannot change what a read gave.

# Planned

- **Mutable collections.** `MArray` and `MList`, with attached operators that update them in place, such as `+=` on an
  `MList`.
- **Cells as values** (`MRef`), which needs [context-qualified types](./context-qualified-types): the type of a value
  that requires `$Memory` says so, and escape falls out of type checking rather than the syntactic check above.
- **`mut` record fields.** The parser reads `mut` before a field name in a record type; nothing checks or compiles it
  yet.
