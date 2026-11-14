---
title: "Inlining"
description: "The inliner in the WebAssembly backend, aggressive for workflows, so a proc block compiles to the matches you would write by hand"
order: 25
---

This page describes the inliner of the WebAssembly backend: its first step is built (`CoreIrInline.fs`,
[#745](https://github.com/nanyx-lang/nanyx/issues/745)), and the rest is the plan below. It is part of the [compilation](./compilation) workstream, and builds on
[context calls](./context-calls), where static closures ([#743](https://github.com/nanyx-lang/nanyx/issues/743)) and
specialising on known members ([#744](https://github.com/nanyx-lang/nanyx/issues/744)) are done.

# The goal: workflows cost nothing

A [workflow](../advanced/workflows) should compile to the code you would have written without it. Inlining is
aggressive for workflows in particular. This `proc` block:

```nanyx fragment
proc {
  def a = try getA()
  def b = try getB()

  return a + b
}
```

should compile to the same code as this:

```nanyx fragment
match getA()
  | #some(a) ->
    match getB()
      | #some(b) -> #some(a + b)
      | other -> other
  | other -> other
```

That means: no closure for the block, none for each continuation, no call through `Proc`'s members, and no boxing of
the members' generic values. The block makes 29 allocations, with the first step of inlining as without it. Before builders became a context and a function
([#1305](https://github.com/nanyx-lang/nanyx/issues/1305)) it made 15: the uniform representation for a member's own
type variables ([#1160](https://github.com/nanyx-lang/nanyx/issues/1160)) boxes each value that crosses a member.

## How the block gets there

`proc` is a handler, a function that runs its block under a `use`:

```nanyx fragment
export def proc: [c] ([Proc & c] () -> #some(b) | r) -> #some(b) | r = { block ->
  use Proc(
    try = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    return = { value -> #some(value) }
    zero = #some()
    for = { items, body, rest -> procFor(items, body, rest) }
  ) in block()
}
```

The block desugars to calls of `Proc`'s members, each keyword's continuation a lambda:
`try(getA(), { a -> try(getB(), { b -> return(a + b) }) })`. Each step below removes one layer:

1. **Inline the handler.** `proc` is small and isn't recursive, so its body replaces the call: the `use` and its
   members now sit in the caller, next to the block.
2. **Inline the block.** The block is a lambda written at the call and called once, so it is replaced by its body.
3. **Members are known.** With the `use` in the same function as the calls, each member is a known lambda, not a
   closure parameter. Specialising (#744) already does this for members it can see. Here inlining makes them visible.
4. **Inline the members.** `try` and `return` are small known lambdas, so each call is replaced by the member's body.
   `try`'s body is a match on its first argument that calls the continuation in one arm.
5. **Inline the continuations.** Each continuation is a lambda written at the call and called once, in the match arm,
   so it is replaced by its body (beta reduction).
6. **Unbox.** The members' generic values are now at known types (`int` here), so the boxing from #1160 isn't needed
   and isn't made.
7. **Remove what's left.** The `use`'s member lambdas, now unused, aren't made. `zero` and `for`, which this block
   doesn't use, go too.

What is left is the nested match above. Each step is an ordinary inliner rule, not a workflow special case. Workflows
are only the reason to make the rules aggressive enough that they all apply.

# Design

**Where it runs.** On the core IR, after lowering and specialising, and before reference counting
(`CoreIrCounting.fs`) and reuse. That way it never has to cancel the dup and drop pairs it would otherwise leave
behind, and reuse sees the simplified code.

**What it inlines:**

- **A lambda called once where it is written,** or bound to a local and called once. This always shrinks the code, so
  it has no size limit. It covers the block and the continuations.
- **A function small enough**, measured in core IR nodes under a limit set by benchmarks. It covers `try`, `return`,
  `println` after specialising, and most attached definitions.
- **Handlers**, functions whose body is a `use ... in f()` of their lambda parameter. They are always inlined, whatever
  their size, because inlining them is what makes their members known.
- **Never** a function into itself, or into a function that it calls back (a cycle). A recursive function is never
  inlined, but its callers may be.

**What inlining exposes, simplified in the same pass:**

- a call through a closure known at that point becomes a direct call, or is inlined in turn;
- a closure or record that nothing reads any more isn't made;
- a `match` on a value whose tag is known at that point takes its one arm;
- a member's boxed value at a known type is unboxed.

**Tail calls stay tail calls.** Every call in tail position is a tail call ([Tail calls](./tail-calls)). An inlined
body's tail calls stay in tail position in the caller, and a call that was in tail position before inlining is in tail
position after.

**Traps keep their place.** Inlined code keeps its original source spans, so a trap reports the line it was written
on, not the caller's.

**Debug and release builds inline alike** (decided). Specialising already runs the same in both, and inlining changes
no behaviour either:

- **The everyday builds use it.** The tests, `nanyx run`, `--check-memory` and the tour examples all run debug builds,
  so they exercise the inliner, and a workflow is as cheap in a debug run as in a release one.
- **Numbers mean the same.** Benchmarks taken from a debug build say what a release build does.
- **Little is lost.** A trap's backtrace loses the frames of inlined functions, but Nanyx shows no backtraces and has no
  debugger yet. If backtraces come, the spans can say which function the code was inlined from, to rebuild them.
- **`--no-inline`** on `nanyx run` and `nanyx build` turns it off, for working on the compiler: it tells at once whether
  a misbehaving program is the inliner's doing. Some tests run programs both ways and check they print the same.

# The first step, as built

`CoreIrInline.inlineCalls` runs over every function of the module after lowering, in `CodeGen.fs`, and before
counting, unless `WasmOptions.Inline` is off (`--no-inline`). It takes the functions so that each comes after those it
calls, and goes over each one up to four times, each time inlining what the time before exposed.

- **What is inlined.** A call to a top-level function, when the function is a handler (its body ends in a call
  through one of its parameters) or has no more than `sizeLimit` (40) core IR nodes. A call through a local that a
  `let` gives a closure over a lifted lambda, when that local is named nowhere else, or the lambda is small. Once a
  handler is inlined, its parameter is the caller's closure, so the next time over the block is inlined too.
- **What is not.** A function in a cycle of calls, found with Tarjan's algorithm over calls and the closures a function
  makes. The start function, whose body must stay a sequence of top-level expressions, takes nothing inlined, and
  nothing is inlined into a guard. A function stops taking more once it has 4,000 nodes.
- **How.** The callee's body is copied in with every variable it binds made new, with a new name, so that none shares a
  local with the caller's: variables of the same kind and name share one. A parameter is the caller's variable when the
  argument is one of its type, and a new local let-bound to the argument otherwise (a lambda's environment, which it
  reads as a record, is bound to the closure). A call is inlined as what a chain ends in or as a `let`'s or `do`'s
  value, a temporary's among them (`acc + double(n)`). Every temporary of the chain still waiting to be used becomes
  a local first, since the inlined body stores before it reads what it is given, and nothing may be stored while a
  temporary waits; temporaries are bound in the order they are evaluated, so the order holds. The body's loops are
  numbered inside the caller's, and a chain that is a `let`'s value is flattened into the chain around it.
- **What is left.** A function inlined wherever it is called is no longer reached from the start, so it is not
  emitted (`CoreIrReach`, which runs after inlining).
- **Tail calls and traps.** A call in tail position in the callee is still in tail position where the call was in
  the caller's tail, so it is still a tail call. A trap's message has its span in it from lowering, so it reports the
  line it was written on.

Measured as the [Context escape](./context-escape#what-it-costs) page measures it, by the allocation counter of a
`--check-memory` build over 100 blocks, a `proc` block of three `try`s and a `return` on `int` payloads still makes
29 allocations a block (`A proc block allocates no more inlined` in `InliningTests.fs`). `proc` and its block are
inlined, but the members reach the block through the adapters and boxes of #1160, which only the next steps remove.

# Plan

1. ~~**The inliner and its first rules**~~ ([#745](https://github.com/nanyx-lang/nanyx/issues/745), done): lambdas
   called once, small functions, handlers, the recursion check, keeping tail calls and spans, in every build, and
   `--no-inline`. Tests show a call inlined and one that isn't (`InliningTests.fs`).
2. **Simplification after inlining:** known calls, unused closures, known matches.
3. **Unboxing members at known types,** after #1160.
4. **The `proc` block above compiles to its nested match,** checked against the WAT of the hand-written match, with
   the allocation count of the generated-code performance review's `proc` benchmark going to zero for the block.
5. **Tune the size limit** against the generated-code benchmarks.
6. **Later:** inlining across modules, and the other builders and handlers in the standard library.

# Open questions

- **The size limit,** and whether a function can ask to be inlined, or not, with an attribute.
- **Code size.** Aggressive inlining grows the module. Is there a budget per function, after which it stops?
- **Across modules.** A function from another module, including the standard library, can only be inlined if its core
  IR is available where it is called. That is true today, since a program compiles as one module, but not once
  packages compile separately.
