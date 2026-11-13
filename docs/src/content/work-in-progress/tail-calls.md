---
title: "Tail calls"
description: "What Nanyx promises about tail calls, and how the compiler keeps that promise"
order: 24
---

Tail calls are built: the WebAssembly backend lowers every call in tail position to `return_call`, the official
linter reports recursion that is not in tail position, and every walk in the standard library is stack-safe. The
tour states the guarantee in [Tail recursion](../tour/functions#tail-recursion). This page keeps the rules, how the
compiler keeps them, and the [plan](#plan) for the rest. It is part of the [compilation](./compilation) workstream,
and its issues carry the `compilation` and `wasm backend` labels.

# Summary

A call in tail position does not use up stack. This is part of the language's meaning, not an optimisation: it
holds for every kind of call (a function calling itself, functions calling each other, a closure, a context
member), and every backend must keep it. The WebAssembly backend keeps it with `return_call` and
`return_call_indirect`.

Nothing else about stack depth is promised. Recursion that is not in tail position uses a frame per call and can
run out of stack.

# Why a guarantee

- **Recursion is how Nanyx loops.** `for` needs a collection that already exists, and `while` needs `Memory`.
  Any other loop is a recursive function, and a loop that may run out of stack depending on its input is not
  usable.
- **Self-recursion alone is not enough.** Turning only a function's calls to itself into a loop (as Scala's
  `@tailrec` and F# do) fails silently for mutual recursion (state machines, parsers), for calls through context
  members, and for continuation-passing code such as [workflows](../advanced/workflows),
  whose keyword methods call the rest of the block as a closure.
- **It is cheap on WebAssembly.** Tail calls are part of WebAssembly 3.0. Wasmtime runs them by default, and
  Chrome and Firefox ship them.
- **It keeps performance predictable,** as the [principles](../foundations/principles)
  ask. A rule the reader can check ("a call in tail position uses no stack") is predictable; an optimisation that
  applies to some shapes of code is not.

The cost is that every backend must have real tail calls. Native code through LLVM can (`musttail`); a JavaScript
or .NET backend would need trampolines, which are slow. Choosing this rules those out as first-class targets.

# Tail position

Tail position is defined on the program after workflows are desugared, so a workflow block needs no rule of its
own.

- The body of a function is in tail position.
- When an `if` is in tail position, so are its `then` and `else` branches.
- When a `match` (including a `\match` in a pipeline) is in tail position, so is the body of each arm. So is
  each arm of an `except`, which is a `match` with an arm that passes the other cases through.
- When a block is in tail position, so is its last expression.
- When `use C(...) in body` is in tail position, so is `body`: loading a context only binds it.
- A pipe `x \f(y)` is the call `f(x, y)`, and is a tail call when it is in tail position.

Nothing else is in tail position. In particular:

- arguments to a call, and operands of an operator (`1 + f(n)`),
- the scrutinee of a `match`, a guard, and the condition of an `if`,
- interpolated values (`"{f(n)}"`),
- list, tuple and record construction (`[x, ...f(xs)]`, `(a = f(n))`),
- the body of a `for` or `while`.

A workflow block becomes `builder.apply({ ... })`, with each keyword's continuation a closure holding the rest of
the block, so the rules above apply to the desugared code. A loop written inside a workflow is stack-safe when the
builder's keyword methods call their continuation in tail position, which is the builder author's choice. A
keyword that runs code after its continuation returns (`defer`) is not.

# Design

## Code generation

When the backend lowers a call in tail position, it emits `return_call $f` instead of `call $f`, and
`return_call_indirect` instead of `call_indirect` for closures and context members. The core IR keeps tail position
in its shape: lowering an expression in tail position (`Tail` in `CoreIrLower.fs`) leaves its last call at the end of
the function's body, and emitting (`CoreIrEmit.fs`) writes a call there as a tail call. A call to a function that
needs contexts passes them as extra arguments, which changes nothing. A lambda with no parameters is lifted into a
function of its own, like any lambda, so its body's tail calls are that function's.

A call in tail position has the type of the function it is in, so a tail call always returns what its caller returns. The
module's start function, which runs top-level expressions and returns nothing, makes no tail calls. Calls to
runtime helpers and host functions stay plain calls; they do not recurse into Nanyx code.

## Reference counting

[Perceus](./memory-management) inserts a `drop` after a variable's last use. A drop placed after a call takes the call out
of tail position, so the two designs have to agree:

- **Drops go before a tail call.** A variable that dies at a tail call is dropped before the call, not after.
- **Arguments to a tail call are owned.** If the callee only borrows an argument the caller owns, the caller has
  to keep it alive until the call returns, and the call is no longer a tail call. Ownership inference makes such a
  parameter owned (as Koka does), or moves the argument into the call.

## Running out of stack

Recursion that is not in tail position can still exhaust the stack. When it does, the runtime should report a Nanyx
error that names the function that recursed, not a WebAssembly backtrace. It does not yet: Wasmtime's trap report,
`wasm trap: call stack exhausted`, is what is shown (plan item 1).

## Checking tail position

The official linter's `nonTailRecursion` rule (`default/nonTailRecursion`) reports a function that calls itself
other than in tail position: a top-level def whose value is a lambda, or a local `rec`. It reports at the call,
and says why the call is not in tail position by naming what is done with its result: the operator it is an
operand of, the function it is passed to, the list it goes into, the name it is bound to, and so on.

- **It is `info` by default.** Recursion that is not in tail position is often right: walking a tree recurses as
  deep as the tree, and it is the plainest way to write a function that builds a list. A project that
  wants every loop to be stack-safe raises it to a warning or an error in its `.nyxp`.
- **Only a function's own calls count.** A call from a lambda written in the function (a workflow block, a `for`
  loop's body, an argument to `list.map`) is a call from that lambda, and is not reported. Nor is mutual recursion:
  the rule looks at one function at a time.
- **It uses the same rules as the backend.** `TailPosition.callsIn` applies them to the syntax, so a call it does
  not report is one the backend lowers to `return_call`.

An opt-in `@tailrec` attribute could later let one function insist on tail recursion whatever the project
configures: the compiler would report each of its calls to itself that is not in tail position as an error. It is
documentation that the compiler checks, not a request to optimise: the guarantee holds with or without it.

## The standard library

Each standard-library function that walks a collection says whether it is stack-safe, and today every one of them
is: `default/nonTailRecursion` reports nothing in `stdlib/nanyx`, which a test checks. Tail recursion modulo cons
(writing a list cell's tail after the recursive call returns, as OCaml and Koka do) would let `list.map` and the
like be written as plainly as they read, but it is an optimisation and not part of the promise; until it exists,
the functions that build a list build it in reverse with an accumulator and reverse the result. `++` on lists is
stack-safe too: its runtime helper copies the cells of its left operand in a loop, writing each new cell's tail once
the next cell exists ([#558](https://github.com/nanyx-lang/nanyx/issues/558)).

# Plan

1. **Stack overflow error.** Report running out of stack as a Nanyx error.
2. **`@tailrec`.** Check the attribute in the type checker, if per-function control turns out to be wanted.

# Open questions

- **Browser targets.** If Nanyx runs in browsers, which engines must it support, and do all of them ship tail
  calls?
- **`defer` and `memory` blocks.** Are there constructs that run code when a scope ends? Anything that does takes
  its body out of tail position, and the docs for it should say so.
