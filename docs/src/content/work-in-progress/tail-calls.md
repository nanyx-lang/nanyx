---
title: "Tail calls"
description: "How the compiler keeps the tail-call guarantee, and what is still open"
order: 24
---

A call in tail position uses no stack. The guarantee and the rules for tail position are in
[Tail recursion](../tour/functions#tail-recursion), why it is a guarantee in
[Compiler optimisations](../foundations/compiler-optimisations#optimisation-model), and the linter's rule in
[Recursion not in tail position](../standard-tools/official-linter#recursion-not-in-tail-position). This page says how
the compiler keeps the guarantee, and what is still open. It is part of the [compilation](./compilation) workstream,
and its issues carry the `compilation` and `wasm backend` labels.

# How the compiler keeps it

## Code generation

The rules for tail position apply to the program after workflows are desugared, so a workflow block needs no rule of
its own: it becomes `builder({ ... })`, with each keyword's continuation a closure holding the rest of the block.

A call in tail position is a `return_call`, and a call through a closure or a context member a `return_call_indirect`.
The core IR keeps tail position in its shape: lowering an expression in tail position (`Tail` in `CoreIrLower.fs`)
leaves its last call at the end of the function's body, and emitting (`CoreIrEmit.fs`) writes a call there as a tail
call. A call to a function that needs contexts passes them as extra arguments, which changes nothing. A lambda with no
parameters is lifted into a function of its own, like any lambda, so its body's tail calls are that function's.

A call in tail position has the type of the function it is in, so a tail call always returns what its caller returns.
The module's start function, which runs top-level expressions and returns nothing, makes no tail calls. Calls to
runtime helpers and host functions stay plain calls; they do not recurse into Nanyx code.

## Reference counting

[Perceus](./memory-management) inserts a `drop` after a variable's last use. A drop placed after a call takes the call
out of tail position, so:

- **Drops go before a tail call.** A variable that dies at a tail call is dropped before the call, not after.
- **Arguments to a tail call are owned.** If the callee only borrowed an argument the caller owns, the caller would
  have to keep it alive until the call returns. So an argument passed in a tail call that the caller owns makes the
  callee own that parameter.
- **A closure that dies at a tail call is passed owned.** A function called through a closure borrows its
  environment, so the caller would have to drop the closure after the call. A tail call of a closure that dies there
  calls the function's owning entry instead (see "Closures have two entries" in
  [Memory management](./memory-management#inserting-counts)).

## Running out of stack

Recursion that is not in tail position can still exhaust the stack. `nanyx run` then reports an error that names the
function, and exits with 1. The runner (`WasmRunner.trapped` in `Runner.fs`) reads a trap whose code is
`StackOverflow` as `RunError.StackOverflow`, with the innermost function on the stack that the source names: it reads
the name the backend gave the function back to the source's, without a specialisation's type arguments (`count__int`
is `count`) and with a local `rec`'s own name (`__rec_go_16` is `go`), and passes over lambdas and runtime helpers,
whose names start with `__`. A library function keeps its module's prefix (`list_fold`). The CLI writes the message
(`ReportRunError` in `Program.cs`). A host other than the CLI reads the trap in its own runtime.

## Checking tail position

The official linter's `nonTailRecursion` rule reports a function that calls itself other than in tail position. It
uses the backend's rules: `TailPosition.callsIn` applies them to the syntax, so a call it does not report is one the
backend lowers to `return_call`. It looks at one function at a time, so a call from a lambda written in the function,
and mutual recursion, are not reported.

## The standard library

Every standard-library function that walks a collection is stack-safe: `default/nonTailRecursion` reports nothing in
`stdlib/nanyx`, which a test checks. The functions that build a list build it in reverse with an accumulator and
reverse the result. `++` on lists copies the cells of its left operand in a loop, writing each new cell's tail once
the next cell exists.

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
