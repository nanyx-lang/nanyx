---
title: "Inlining"
description: "The inliner in the WebAssembly backend, aggressive for workflows, so a proc block compiles to the matches you would write by hand"
order: 25
---

This page describes the inliner of the WebAssembly backend: its first four steps are built (`CoreIrInline.fs`,
[#745](https://github.com/nanyx-lang/nanyx/issues/745), and `CoreIrSimplify.fs`,
[#1339](https://github.com/nanyx-lang/nanyx/issues/1339), [#1340](https://github.com/nanyx-lang/nanyx/issues/1340) and
[#1341](https://github.com/nanyx-lang/nanyx/issues/1341)), so the `proc` block below compiles to its nested match, and
the rest is the plan below. It is part of the [compilation](./compilation) workstream, and builds on
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
the members' generic values. The block makes 29 allocations without inlining, and with it none of its own: it compiles
to the same WebAssembly as the match. Before builders became a context and a function
([#1305](https://github.com/nanyx-lang/nanyx/issues/1305)) it made 15: the uniform representation for a member's own
type variables ([#1160](https://github.com/nanyx-lang/nanyx/issues/1160)) boxes each value that crosses a member.

## How the block gets there

`proc` is a handler, a function that runs its block under a `use`:

```nanyx fragment
export def proc: [c] ([Proc, c] () -> #some(b) | r) -> #some(b) | r = { block ->
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
   and isn't made. A box is a closure whose function returns the value, so this needs no rule of its own: once a box
   is made and read in the same branch, reading it is a call through a known closure, inlined, and the box is left
   unread. What brings the two into one branch is copying a `match` into the branches of the `if` or `match` whose tag
   it tests: `getA`'s `if` gives `#some(n)` in one branch, where `try`'s match then takes its arm.
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
- a `match` on what an `if` or another `match` gives is copied into its branches, where that is known;
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

# As built

`CoreIrInline.inlineCalls` runs over every function of the module after lowering, in `CodeGen.fs`, and before
counting, unless `WasmOptions.Inline` is off (`--no-inline`). It takes the functions so that each comes after those it
calls, and goes over each one up to sixteen times, each time simplifying (`CoreIrSimplify.simplify`) and then inlining
what the time before exposed, and simplifies it once more at the end. Once nothing more is inlined anywhere, each
function's locals that are read once, at once, are made temporaries (`CoreIrSimplify.withTemporaries`).

## Inlining

- **What is inlined.** A call to a top-level function, when the function is a handler (its body ends in a call
  through one of its parameters), or a lambda the caller makes a closure over and calls once, or has no more than
  `sizeLimit` (40) core IR nodes. A call through a closure is inlined once simplifying has made it a call of the
  lambda, with the closure as its environment. Once a handler is inlined, its parameter is the caller's closure, so
  the next time over the block is inlined too, then the members its `use` binds, then the continuations they are
  given.
- **What is not.** A function in a cycle of calls, found with Tarjan's algorithm over calls and the closures a function
  makes, and the start function itself. Nothing is inlined into a guard. The start function takes inlining as any
  other does ([#1469](https://github.com/nanyx-lang/nanyx/issues/1469)): the host's `run` is inlined into it, so the
  closure over the entry and the members of the host's `use` are known there, the entry is called directly, and a
  member the entry never reads is not reached. A program that only prints has none of the code that reads standard
  input: hello world went from 60,418 bytes of WAT to 9,996. A function stops taking more once it has 4,000 nodes.
- **How.** The callee's body is copied in with every variable it binds made new, with a new name, so that none shares a
  local with the caller's: variables of the same kind and name share one. A parameter is the caller's variable when the
  argument is one of its type, and a new local let-bound to the argument otherwise (a lambda's environment, which it
  reads as a record, is bound to the closure, a tag to a parameter whose type has other tags, and a function that gives or takes tags to one whose type has other tags there, since tags are held alike). A call is inlined
  as what a chain ends in or as a `let`'s or `do`'s value, a temporary's among them (`acc + double(n)`). Every
  temporary of the chain still waiting to be used becomes a local first, since the inlined body stores before it reads
  what it is given, and nothing may be stored while a temporary waits; temporaries are bound in the order they are
  evaluated, so the order holds. The body's loops are numbered inside the caller's, and a chain that is a `let`'s value
  is flattened into the chain around it. The walk over the IR that this needs is shared with simplifying
  (`CoreIrRewrite.fs`).
- **What is left.** A function inlined wherever it is called is no longer reached from the start, so it is not
  emitted (`CoreIrReach`, which runs after inlining).
- **Tail calls and traps.** A call in tail position in the callee is still in tail position where the call was in
  the caller's tail, so it is still a tail call. A trap's message has its span in it from lowering, so it reports the
  line it was written on.

## Simplifying

`CoreIrSimplify.simplify` walks a function's body with what is known about each variable at each point: the closure or
tag its binding makes, or another variable's value it is given, and, inside a `match` arm, the tag the arm's pattern
tests and the tags the arms before it tested and failed. From that:

- **A call through a known closure** is a direct call of the lambda, with the closure as its environment, which the
  inliner may inline in turn. A call that stays (a lambda that calls the function back, say) is a `call`, not a
  `call_indirect`.
- **A field of a known closure's environment** is the value the closure captured there, so an inlined lambda, or an
  adapter of #1160, reads the caller's variables and not the block.
- **A `match` on known scrutinees** takes the arm they match, binding the names its patterns bind; an arm they cannot
  match is left out, and a scrutinee every arm matches whatever it is is no longer tested.
- **The code after a branch** (an `if` or a `match`) that gives a tag in one of its branches is copied into each
  branch when it matches on that tag, so that each copy knows which tag it is (case-of-case). Each copy but the first
  has its variables made new. It is kept only when the copies, simplified, grow the code by no more than `copyLimit`
  (40) nodes, as when all but one of them come to little. Within a copy being tried nothing more is copied, so a trial
  cannot grow without end; the next pass goes on from there.
- **A variable given another variable's value** of its type is that variable: what follows reads the other, and the
  binding goes. Not a parameter on the heap, which its function may only borrow: a match on the variable that takes it
  over can reuse its memory, and a match on the parameter could not.
- **A chain that ends in a variable** it gave another variable's value, or a constant, or the value of its last link,
  ends in that value.
- **A binding that nothing reads** is not made when its value has no effect (no call, no write, and no operator that
  can trap), and a closure, tag or record that only it held goes with it.

A value is only read again somewhere new from a constant, or from a variable that nothing assigns, that has its local
to itself (no other variable of its kind has its name), and that is not a temporary. Two rules make temporaries into
what can be read: a temporary given at once to a variable is that variable's value, and a temporary a tag is made of,
given at once to a variable, becomes a local. A temporary whose value is a chain keeps the chain whole, since it is
evaluated where it is used. A loop's own locals are not read anywhere new either: its body reads them through bindings
of its own.

**Locals read at once.** After inlining, a local that is read once, by what the very next link stores or does or by
what the chain ends in, is a temporary: it is computed where it is read, on the stack, with no local. Nothing happens
between the two, so nothing moves. The reader may not be a temporary from lowering, whose value is computed later
still, where it is read, but may be one made by this rule. It runs only once nothing more is inlined, since a temporary
can be read only where it is computed, and would hide its value from a caller inlining the function. A local the
source names (a block's `def`) is made one too: `y` in `def y = x + 1` followed by `y + 1`.

## What it costs

Measured as the [Context escape](./context-escape#what-it-costs) page measures it, by the allocation counter of a
`--check-memory` build over 100 blocks, a `proc` block of three `try`s and a `return` on `int` payloads makes 29
allocations a block without inlining, and none with it (`A proc block allocates fewer inlined` in `InliningTests.fs`).
`proc`, the block, the members, the continuations and `getA` are inlined, none of them is a closure, and nothing is
boxed: the block is three nested `if`s, on `n > 0`, `n + 1 > 0` and `n + 2 > 0`, and its function is the same
WebAssembly as the nested match written by hand, but for the names of its locals (`A proc block compiles to the nested
match written by hand`). On its own it allocates only the `#some` it returns; the benchmark's loop inlines it too, and
its match on that `#some` is copied into the block's branches, so not even that is made. Timed by `run(100000)` from the
host, the block takes about 3 ns inlined and 244 ns not.

Step by step: 29 allocations with the first step (#745); 17, and about 92 ns, with simplifying (#1339), the boxing
being all that was left; 1, and about 9 ns, with case-of-case (#1340); none, and about 3 ns, with copying variables and
locals read at once (#1341).

Inlining with simplifying also leaves out of a program what only unused closures needed: a host whose `use` binds
members the program never calls no longer makes their closures, so their functions and the helpers they use are not
emitted.

# Plan

1. ~~**The inliner and its first rules**~~ ([#745](https://github.com/nanyx-lang/nanyx/issues/745), done): lambdas
   called once, small functions, handlers, the recursion check, keeping tail calls and spans, in every build, and
   `--no-inline`. Tests show a call inlined and one that isn't (`InliningTests.fs`).
2. ~~**Simplification after inlining**~~ ([#1339](https://github.com/nanyx-lang/nanyx/issues/1339), done): known
   calls, unused closures, known matches (`CoreIrSimplify.fs`, tests in `InliningTests.fs`).
3. ~~**Unboxing members at known types**~~ ([#1340](https://github.com/nanyx-lang/nanyx/issues/1340), done): by
   copying a match into the branches whose tag it tests, which brings each box and its reading into one branch.
4. ~~**The `proc` block above compiles to its nested match**~~
   ([#1341](https://github.com/nanyx-lang/nanyx/issues/1341), done): checked against the WAT of the hand-written match,
   with the `proc` benchmark's allocations at zero.
5. **Tune the size limit** against the generated-code benchmarks.
6. **Later:** inlining across modules, and the other builders and handlers in the standard library.

# Open questions

- **The size limit,** and whether a function can ask to be inlined, or not, with an attribute.
- **Code size.** Aggressive inlining grows the module. Is there a budget per function, after which it stops?
- **Across modules.** A function from another module, including the standard library, can only be inlined if its core
  IR is available where it is called. That is true today, since a program compiles as one module, but not once
  packages compile separately.
