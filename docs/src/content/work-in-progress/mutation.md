---
title: "Mutation"
description: "Local cells today, and the rest of the plan for mutation under $Memory"
order: 2
---

The design of mutation is on [Memory](../advanced/memory). Its first step, local cells, is done
([#756](https://github.com/nanyx-lang/nanyx/issues/756)); this page is what is left.

# What works

- The prelude declares the nominal context `$Memory` and `memory`, which runs a block with it.
- `mut e` makes a cell in a `memory` block, `:=` changes one, and a cell's name reads it. Cells are not values: nothing
  can hold one.
- A lambda in the block may use the block's cells. A block whose result is, or holds, a lambda that uses one is an
  error.
- A cell no lambda captures is a WebAssembly local; a captured one is a reference-counted box the lambdas share.
- `while condition -> body` runs its body for as long as its condition, which may read cells, is true
  ([#957](https://github.com/nanyx-lang/nanyx/issues/957)). The parser makes it a call to `__while` (`WhileSyntax`);
  neither part is a lambda, so the checker (`Loops.inferWhile`) types them where the loop is written, and the core IR
  has a `While` node that evaluates both again each time round, borrowing what they use from outside.

# How it works

- **Type checking** (`compiler/Nanyx.Compiler/MemoryCells.fs`). The block of a call to the prelude's `memory` is
  checked with a name in the environment no code can write, which is what `mut` looks for. A def whose value is
  `mut e` is a cell: its symbol is in `InferState.Cells`, and it is not generalised. `:=` checks that its target
  resolves to a cell, and adds an assignability constraint reported at the `:=`, whose mismatches get their own code.
  A lambda does not yet carry `$Memory` in its type, so escape is checked syntactically: the block's result, if its
  type holds a function, must not hold a lambda that names a cell from outside itself, or a local function that does.
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
