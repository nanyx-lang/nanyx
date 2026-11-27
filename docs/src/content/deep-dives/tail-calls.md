---
title: "Tail calls"
description: "How the compiler keeps the tail-call guarantee"
order: 7
---

A call in tail position uses no stack. The guarantee and the rules for tail position are in
[Tail recursion](../tour/functions#tail-recursion), why it is a guarantee in
[Compiler optimisations](../meta/compiler-optimisations#optimisation-model), and the linter's rule in
[Recursion not in tail position](../standard-tools/official-linter#recursion-not-in-tail-position). This page says how
the compiler keeps the guarantee. What is still planned or open is in issues:
`@tailrec` ([#2723](https://github.com/nanyx-lang/nanyx/issues/2723)) and tail recursion modulo cons
([#2724](https://github.com/nanyx-lang/nanyx/issues/2724)).

# Code generation

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

The guarantee holds in browsers too, in the [playground](../standard-tools/playground) and the tour's examples:
`return_call` is in Chrome, Edge and Firefox, and in Safari from 18.2. A browser without WebAssembly tail calls gets a
message saying so, not a program that runs out of stack ([Playground](./playground#the-page)).

# Reference counting

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

# Running out of stack

Recursion that is not in tail position can still exhaust the stack. `nanyx run` then reports an error that names the
function, and exits with 1. The runner (`WasmRunner.trapped` in `Runner.fs`) reads a trap whose code is
`StackOverflow` as `RunError.StackOverflow`, with the innermost function on the stack that the source names: it reads
the name the backend gave the function back to the source's, without a specialisation's type arguments (`count__int`
is `count`) and with a local `rec`'s own name (`__rec_go_16` is `go`), and passes over lambdas and runtime helpers,
whose names start with `__`. A library function keeps its module's prefix (`list_fold`). The CLI writes the message
(`ReportRunError` in `RunProgram.cs`). A host other than the CLI reads the trap in its own runtime.

A program may use 512 KiB of stack (`ProgramStack.guestStack` in `ProgramStack.fs`). Wasmtime runs it on the native
stack of the thread that calls into it, and traps once the program has used that much, but only if the thread has that
much left: otherwise the program reaches the end of the thread's stack first, which Linux and macOS turn into the same
trap but which on Windows stops the whole process ([#2708](https://github.com/nanyx-lang/nanyx/issues/2708)). So the
runner, the server (for a program whose host answers HTTP requests, each request's) and the componentizer each run a
program on a thread of its own, of 8 MB
(`ProgramStack.threadStack`), whichever thread called them: Windows gives its main thread 1 MB.

# Code at the end of a scope

A construct that runs code when its scope ends runs it after the last call in its body, so that call is no tail call.
Nanyx has no `defer`; these are the constructs, each with a test in `RunTailCallTests`, `StackOverflowTests` or
`RunScopeTailCallTests`:

- **A `memory { ... }` block** frees its region when its body is done (see `CoreIrRegions`), so a call last in the body
  is not a tail call.
- **A def that starts tasks** waits for them before it returns, so a call last in it is not a tail call, but for a call
  of another def that starts tasks in the same module, whose tasks join the caller's scope and which stays a tail call
  (see `TaskScopes`).
- **`use C(...) in body`** runs nothing when it ends: the instance is only in scope for `body`, so `body` is in tail
  position. So does a scoped context's `use`, the one `memory` makes included; it is the region's freeing that
  `memory` adds.
- **Reference counting** drops a variable after its last use, but never after a tail call (see above).

# Checking tail position

The official linter's `nonTailRecursion` rule reports a function that calls itself other than in tail position. It
uses the backend's rules: `TailPosition.callsIn` applies them to the syntax, so a call it does not report is one the
backend lowers to `return_call`. It looks at one function at a time, so a call from a lambda written in the function,
and mutual recursion, are not reported. A `memory` block's lambda is the exception: the block runs in the function, so a
call in it is reported, as no tail call.

# The standard library

Every standard-library function that walks a collection is stack-safe: `default/nonTailRecursion` reports nothing in
`stdlib/nanyx`, which a test checks. The functions that build a list build it in reverse with an accumulator and
reverse the result. `++` on lists copies the cells of its left operand in a loop, writing each new cell's tail once
the next cell exists.
