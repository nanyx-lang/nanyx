---
title: "Inlining"
description: "Proposal: an inliner in the WebAssembly backend, aggressive for workflows, so a proc block compiles to the matches you would write by hand"
order: 25
---

This page proposes an inliner for the WebAssembly backend. The backend has none today: every call in the core IR is
compiled as a call. It is part of the [compilation](./compilation) workstream, and builds on
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
the members' generic values. Today the block makes 29 allocations. Before builders became a context and a function
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

# Plan

1. **The inliner and its first rules** ([#745](https://github.com/nanyx-lang/nanyx/issues/745)): lambdas called once,
   small functions, handlers, the recursion check, keeping tail calls and spans, in every build, and `--no-inline`.
   Tests show a call inlined and one that isn't.
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
