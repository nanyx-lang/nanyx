---
title: "Tail calls"
description: "What is planned and open about tail calls"
order: 24
---

A call in tail position uses no stack. The guarantee and the rules for tail position are in
[Tail recursion](../tour/functions#tail-recursion), why it is a guarantee in
[Compiler optimisations](../foundations/compiler-optimisations#optimisation-model), and the linter's rule in
[Recursion not in tail position](../standard-tools/official-linter#recursion-not-in-tail-position). How the compiler
keeps it is in [Tail calls](../compiler-internals/tail-calls) in the Compiler internals section. This page keeps what is
still planned and open. It is part of the [compilation](./compilation) workstream,
and its issues carry the `compilation` and `wasm backend` labels.

# Plan

1. **`@tailrec`**, if per-function control turns out to be wanted: an opt-in attribute that makes the compiler report
   each of the function's calls to itself that is not in tail position as an error, whatever the project's linters say.
   It documents the function; the guarantee holds with or without it.
2. **Tail recursion modulo cons**, writing a list cell's tail after the recursive call returns, as OCaml and Koka do.
   It would let `list.map` and the like be written as plainly as they read. It is an optimisation, not part of the
   guarantee.

# Open questions

- **Browser targets.** If Nanyx runs in browsers, which engines must it support, and do all of them ship tail calls?
- **`defer` and `memory` blocks.** Are there constructs that run code when a scope ends? Anything that does takes its
  body out of tail position, and the docs for it should say so.
