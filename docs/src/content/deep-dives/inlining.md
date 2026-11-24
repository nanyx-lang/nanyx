---
title: "Inlining"
description: "How the WebAssembly backend's inliner and simplifier work"
order: 6
---

This page says how the inliner of the WebAssembly backend works (`CoreIrInline.fs` and `CoreIrSimplify.fs`). What it
means for users, and the `proc` block it compiles to a nested match, are in
[Inlining](../meta/compiler-optimisations#inlining). It builds on [context calls](./context-calls). What is still
planned or open is on [Inlining](../work-in-progress/inlining) in Work in progress.

# How a workflow block compiles away

A workflow's block desugars to calls of its context's members, each keyword's continuation a lambda:
`proc { def a = try getA() ... }` is `proc({ try(getA(), { a -> ... }) })`, and `proc` runs the block under a
`use Proc(...)`. No rule is special to workflows; the ordinary rules below apply in turn:

1. `proc` is a handler called once, so it is inlined, and the `use` and its members sit next to the block.
2. The block is a lambda written at the call and called once, so it is inlined.
3. With the `use` in the same function as the calls, each member is a known lambda, and `try` and `return` are
   inlined.
4. Each continuation is a lambda called once, in a match arm, so it is inlined too.
5. The members' generic values, boxed for a member generic in variables of its own, are made and read in the same
   branch once a `match` is copied into the branches of the `if` or `match` whose tag it tests, so the box is read
   through a known closure, inlined, and left unread.
6. The `use`'s member lambdas, which nothing reads after these steps, are not made.

# Where it runs

`CoreIrInline.inlineCalls` runs over every function of the module after lowering and specialising, in `CodeGen.fs`,
and before reference counting (`CoreIrCounting.fs`) and reuse, so it never has to cancel the dup and drop pairs it
would otherwise leave, and reuse sees the simplified code. It is off with `WasmOptions.Inline` (`--no-inline` on
`nanyx run` and `nanyx build`), which tells at once whether a misbehaving program is the inliner's doing; some tests
run programs both ways and check they print the same. Debug and release builds inline alike.

In a program with a start function it goes over only the functions the start reaches once calls are inlined into it,
and leaves the rest for `CoreIrReach` to drop. A host's instance makes a closure over every member of its contexts,
which inlining shows most programs never call: hello world is lowered to 148 functions and reaches 1. Before a call is
looked at, the function it calls has been gone over, so each function is inlined into as it would be if every function
were. A module with no start has every function gone over, and keeps every function unless a component exports some:
a library component keeps what its exports reach (`CoreIrReach.reachedFrom`).

It takes the functions so that each comes after those it calls, and goes over each one up to sixteen times, each time
simplifying (`CoreIrSimplify.simplify`) and then inlining what the time before exposed, and simplifies it once more at
the end. Once nothing more is inlined anywhere, each function's locals that are read once, at once, are made
temporaries (`CoreIrSimplify.withTemporaries`).

## Inlining

- **What is inlined.** A call to a top-level function, when the function is a handler (its body ends in a call
  through one of its parameters) that the caller calls once, or a lambda the caller makes a closure over and calls
  once, or has no more than `sizeLimit` (40) core IR nodes. A handler the caller calls more than once is inlined when
  it has no more than `handlerLimit` (160) nodes, so that code doesn't double with each level of handlers that call the
  one below twice ([#1664](https://github.com/nanyx-lang/nanyx/issues/1664)). A call through a closure is inlined
  once simplifying has made it a call of the lambda, with the closure as its environment. Once a handler is inlined,
  its parameter is the caller's closure, so the next time over the block is inlined too, then the members its `use`
  binds, then the continuations they are given.
- **A function the module calls once.** A function that the whole module, as it is lowered, calls once and makes no
  closure over (`calledOnce`) is inlined whatever its size, since its body only moves. This is how an `@entry` of any
  size, ending in anything, gets the host's `Console` members: it is inlined into the start function, where they are
  known, so its `println`s write directly and the code that reads standard input is never reached
  ([#1883](https://github.com/nanyx-lang/nanyx/issues/1883)). Only its first inlining is for this reason (`placed`):
  the function it was inlined into may itself be inlined in several places, and then the copies follow the other
  rules. A function a component exports inlines whatever it calls once, whatever its size, too: an export that requires
  contexts calls its host's `provide` once, which is inlined into it, so it keeps only the contexts it uses and imports
  only what their members call ([#2326](https://github.com/nanyx-lang/nanyx/issues/2326)). It keeps every member of
  those contexts, since the export is passed the instance; an entry, inlined into the start, keeps only those it calls.
- **What is not.** A function in a cycle of calls, found with Tarjan's algorithm over calls and the closures a function
  makes. Nothing is inlined into a guard. A function stops taking more once it has 4,000 nodes. The start function
  takes inlining as any other does: the host's `run` is inlined into it, so the closure over the entry and the members
  of the host's `use` are known there, the entry is called directly, and a member the entry never reads is not
  reached.
- **How.** The callee's body is copied in with every variable it binds made new, with a new name, so that none shares
  a local with the caller's: variables of the same kind and name share one. A parameter is the caller's variable when
  the argument is one of its type, and a new local let-bound to the argument otherwise (a lambda's environment, which
  it reads as a record, is bound to the closure, a tag to a parameter whose type has other tags, and a function that
  gives or takes tags to one whose type has other tags there, since tags are held alike). A body whose type has fewer
  tags than the function declares, as one that declares an error it never makes, is inlined too, and given the
  declared type ([#1882](https://github.com/nanyx-lang/nanyx/issues/1882)). A call is inlined as what a
  chain ends in or as a `let`'s or `do`'s value, a temporary's among them (`acc + double(n)`), and so in the branches
  of an `if` or a `match` that is a temporary's value, as the right operand of an `and` or an `or` is
  ([#2486](https://github.com/nanyx-lang/nanyx/issues/2486)). Every temporary of the
  chain still waiting to be used becomes a local first, since the inlined body stores before it reads what it is
  given, and nothing may be stored while a temporary waits; temporaries are bound in the order they are evaluated, so
  the order holds. The body's loops are numbered inside the caller's, and a chain that is a `let`'s value is flattened
  into the chain around it. The walk over the IR that this needs is shared with simplifying (`CoreIrRewrite.fs`).
- **What is left.** A function inlined wherever it is called is no longer reached from the start, so it is not
  emitted (`CoreIrReach`, which runs after inlining).
- **Tail calls and traps.** A call in tail position in the callee is still in tail position where the call was in
  the caller's tail, so it is still a tail call ([Tail calls](./tail-calls)). A trap's message has its span in it from
  lowering, so it reports the line it was written on.

## Simplifying

`CoreIrSimplify.simplify` walks a function's body with what is known about each variable at each point: the closure or
tag its binding makes, or another variable's value it is given, and, inside a `match` arm, the tag the arm's pattern
tests and the tags the arms before it tested and failed. From that:

- **A call through a known closure** is a direct call of the lambda, with the closure as its environment, which the
  inliner may inline in turn. A call that stays (a lambda that calls the function back, say) is a `call`, not a
  `call_indirect`.
- **A field of a known closure's environment** is the value the closure captured there, so an inlined lambda, or a
  member's adapter, reads the caller's variables and not the block.
- **A field of a record the function makes** is the value it was made with, and a `match` that takes the record apart
  binds those values, when nothing else uses the record; then nothing reads it, and it is not made. A function whose
  lone parameter is a tuple takes the tuple's elements and makes the tuple of them, so `{ p -> p.0 }` and
  `{ (a, _) -> a }` allocate nothing (#1884). A record that is passed or read whole is left alone, since what it is
  passed to may write it, as a cell's or an `MList`'s helpers do.
- **A `match` on known scrutinees** takes the arm they match, binding the names its patterns bind; an arm they cannot
  match is left out, and a scrutinee every arm matches whatever it is is no longer tested.
- **The code after a branch** (an `if` or a `match`) that gives a tag in one of its branches is copied into each
  branch when it matches on that tag, so that each copy knows which tag it is (case-of-case). Each copy but the first
  has its variables made new. It is kept only when the copies, simplified, grow the code by no more than `copyLimit`
  (40) nodes, as when all but one of them come to little. Within a copy being tried nothing more is copied, so a trial
  cannot grow without end; the next pass goes on from there. Copies that could not come to so little are never made:
  the sizes of a chain's links, and which links read which variables, are worked out once for the chain
  (`ChainShape`), and the rest of the chain is copied only if the links that could change in a copy are big enough
  that the others, copied into every branch, fit the limit. The links that could change are those that read the
  variable, or a variable such a link binds that something can be known of (not a number, string or `bool`), those
  whose variable only they read, and those that give a variable another's value. Without this, a function with a long
  run of tags made and matched would take time that grows with the square of its length
  ([#1847](https://github.com/nanyx-lang/nanyx/issues/1847)).
- **A variable given another variable's value** of its type is that variable: what follows reads the other, and the
  binding goes. Not a parameter on the heap, which its function may only borrow: a match on the variable that takes it
  over can reuse its memory, and a match on the parameter could not. The walk keeps the variables it renames this way
  and renames each link as it reaches it, so a function with many such bindings takes time in proportion to its
  length.
- **A chain that ends in a variable** it gave another variable's value, or a constant, or the value of its last link,
  ends in that value.
- **A binding that nothing reads** is not made when its value has no effect (no call, no write, and no operator that
  can trap), and a closure, tag or record that only it held goes with it.

A value is only read again somewhere new from a constant, or from a variable that nothing assigns, that has its local
to itself (no other variable of its kind has its name), and that is not a temporary. Three rules make temporaries into
what can be read: a temporary given at once to a variable is that variable's value; a temporary a tag is made of,
given at once to a variable, becomes a local; and the temporaries a record is made of, bound one after another just
before it in the order it reads them, become locals when the record is given to a variable and only read a field at a
time. The last is what lets a `use`'s instance, a record of its members' closures, give back the closure a member was
made with, so that a call of the member, once its block is inlined, is a direct call that the inliner inlines in turn,
and neither the closure nor the record is made ([#2578](https://github.com/nanyx-lang/nanyx/issues/2578)). A temporary whose value is a chain keeps the chain whole, since it is
evaluated where it is used. A loop's own locals are not read anywhere new either: its body reads them through bindings
of its own.

**Locals read at once.** After inlining, a local that is read once, by what the very next link stores or does or by
what the chain ends in, is a temporary: it is computed where it is read, on the stack, with no local. Nothing happens
between the two, so nothing moves. The reader may not be a temporary from lowering, whose value is computed later
still, where it is read, but may be one made by this rule. It runs only once nothing more is inlined, since a temporary
can be read only where it is computed, and would hide its value from a caller inlining the function. A local the
source names (a block's `def`) is made one too: `y` in `def y = x + 1` followed by `y + 1`.

## What it costs

Measured by the allocation counter of a
`--check-memory` build over 100 blocks, a `proc` block of three `try`s and a `return` on `int` payloads makes 29
allocations a block without inlining, and none with it (`A proc block allocates fewer inlined` in `InliningTests.fs`).
Its function is the same WebAssembly as the nested match written by hand, but for the names of its locals (`A proc
block compiles to the nested match written by hand`). Timed by `run(100000)` from the host, the block takes about 3 ns
inlined and 244 ns not.
