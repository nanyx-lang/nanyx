---
title: "Effect handlers"
description: "Contexts whose members a handler runs at once or captures: Emit's pushing builders, yield on async's state machines, and what a spike measured"
order: 7
---

This page is about effect operations in the sense of Koka: calls to a member of a context whose handler, the
instance a `use` loads, runs the code after the call at once (a pushing, or tail-resumptive, operation) or captures it
to run later. It is the "effect handlers in general" question on
[Concurrency and crash boundaries](./concurrency-and-crash-boundaries#open-questions). Two cases are in hand:

- **Pushing: `Emit(a)`.** Decided on 2026-11-24 and being built
  ([#2566](https://github.com/nanyx-lang/nanyx/issues/2566)): the builders push through a new prelude context.
- **Capturing: `yield` on async's state machines.** Proposed, and tested by a spike (below):
  [#2426](https://github.com/nanyx-lang/nanyx/issues/2426), with what is left of
  [#2427](https://github.com/nanyx-lang/nanyx/issues/2427).

# Why

Today `yield` has one meaning. Any lambda with a `yield` in it is rewritten, before type inference, into a lazy
sequence: `yield x` with the rest `k` is `seq.cons(x, { k })` ([Lazy sequences](../deep-dives/lazy-sequences#yield)).
That has three costs:

- **Builders pay for laziness they never use.** `list.build`, `list.select`, `Array.build` and the others pull the
  whole sequence at once, but each value yielded still costs a sequence step and a closure: `list.select` makes about
  seven blocks for each value it yields, where building the list by hand makes one.
- **`yield` must be written in the lambda itself.** A recursive tree walk can't yield from the functions it calls,
  since the rewrite only sees one lambda.
- **It is a special case.** The `#yield` keyword kind writes calls to the prelude's `seq.cons`, `__seqLast`,
  `seq.flatMap`, `seq.append` and `seq.delay` (`WorkflowDesugaring`), so a user's own `#yield` keyword can only ever
  make a `seq`. Nanyx should let a library define what a keyword does, without parts that only the standard types get.

Giving one `yield` two meanings, chosen by what the lambda is passed to, doesn't work: the rewrite runs before
inference, so the choice would need desugaring driven by types, and `xs \select { ... }` finds its `select` by the
type of `xs`.

So pushing and capturing are two contexts, `Emit` and `Yield`, and stay so (decided 2026-11-24). One keyword whose
kind each builder's context declares, one with a plain `yield: a -> ()` and another with a capturing `yield` that
takes the continuation (Koka's `fun` and `ctl`), would come back only if the rewrite were ever driven by types.

# Pushing: `Emit(a)`

Decided, and being built in [#2566](https://github.com/nanyx-lang/nanyx/issues/2566). A pushing operation is a
context of its own, so nothing has to choose between meanings:

```nanyx fragment
export context Emit(a) = (emit: a -> ())
```

- **A body calls `emit(x)`,** a plain context call. It returns, and the code after it carries on: loops stay loops,
  and nothing is allocated for the rest of the block. This is Koka's tail-resumptive (`fun`) operation.
- **The builders push.** `list.select`, `list.build`, `Array.build`, `MList.build` and the set and map builders take
  `[c, Emit(b)] a -> ()` and run it under a handler that appends: `use Emit(emit = { y -> result += y })`.
- **`Emit` is loadable,** so a user writes their own handler, a visitor or a tree walk that pushes into a callback,
  and `emit` works through higher-order calls like any context requirement.
- **No keyword yet.** `emit! x` would need a keyword kind for a plain call, which is undecided.
- **`seq { }` stays lazy,** its block typed `[Yield(a)] () -> ()`, and `yield` in it is rewritten as today.

# Capturing: `yield` as an effect operation

Proposed. `Yield(a)` keeps the capturing meaning (Koka's `ctl`): its operation passes the rest of the block to the
handler as a continuation, `k`, to call later, once or never. A lazy sequence is a handler that gives back
`#next(y, rest)`, where pulling `rest` calls `k`. Instead of the rewrite:

- **The rest of the block is captured the way async waits.** A function that requires a context whose members may
  capture is compiled to a state machine, as a function that requires `Async` is ([Async](../deep-dives/async#how-tasks-are-compiled)).
  A call of such a member is a split point. When the handler in force captures, the call stops, and the frames saved
  on the way out, up to the `use` that loaded the handler, are `k`. The `use` is the delimiter. When the handler pushes,
  the call returns, at the cost of the check after it, as an async call that doesn't stop costs today.
- **`seq` becomes a library function,** a capturing handler like any user's. The `#yield` keyword kind and the
  rewrite's hard-wired calls go, and `yield` reaches its handler from any depth, through the functions a block calls.
- **`Async` becomes the first such context.** The scheduler is the outermost handler, and the host's waits are its
  operations.

Nothing chooses a meaning before inference: whether a function is a state machine is read from its type after
inference, as `IrFunction`'s `waits` is today.

# What the spike showed

A spike on the branch `richard/effect-handlers-spike` (not for merging; its programs are in `spike/effect-handlers`)
tested both halves.

## Pushing

`list.select` written with a context of its own, `context Emit(a) = (emit: a -> ())` as #2566 adds, and a builder
that loads it, compiles on main today, once its `use` writes the instance's type, `use Emit(b)(emit = { y -> MList.add(result, y) })`
([#2550](https://github.com/nanyx-lang/nanyx/issues/2550) is the bug without it). Over 300,000 elements yielding 1.5
values each:

| | allocations beyond the input | bytes allocated |
|---|---|---|
| today's `list.select` with `yield` | 3,150,015 | 78.0 MB |
| a pushing `select` | 450,009 | 33.6 MB |

That is one block, the list cell, per value yielded: what building the list by hand makes. A handler that holds the
builder's `MList` can't leave its `memory` block: returning it is `NYX0233`, as for any cell.

## Capturing

A memberless `Gen` context in the backend's waiting set, an intrinsic `gen.yield` that stops as a wait does, and two
intrinsics that run and resume a block and hand back the frame it stopped in, made `generate { ... }` a lazy
sequence in about 120 lines, with no change to the state-machine lowering itself.

- The code after a `gen.yield` runs only when the sequence is pulled that far.
- A loop that yields is a loop, not a `seq.flatMap`.
- A recursive `walk: [Gen] Tree -> ()` that yields between its recursive calls gives an in-order walk of a tree, which
  `seq.take` stops partway. Today's rewrite can't express it.

Summing `1..300000`:

| | allocations | per element | bytes allocated |
|---|---|---|---|
| the range alone | 1,200,004 | 4 | 21.6 MB |
| today's `seq { for 1..n \| i -> yield i }` | 3,000,010 | 10 | 51.6 MB |
| the spike's `generate { for 1..n \| i -> gen.yield(i) }` | 2,100,007 | 7 | 44.4 MB |

The spike's three extra blocks per element are frames, allocated at each stop and freed at each resume; a frame that
stops again as soon as it is resumed could keep its block.

# Plan

| Step | Issue | Stage |
|---|---|---|
| A `use` whose instance's type is settled after it finds its members | [#2550](https://github.com/nanyx-lang/nanyx/issues/2550) | Working set |
| `Emit(a)`, and the builders pushing through it | [#2566](https://github.com/nanyx-lang/nanyx/issues/2566) | Working set |
| A frame can be dropped and copied: a drop function and a copy function for each state, beside its resume entry, from the types the emitter knows where it saves the variables | | proposed |
| Members that may capture: split points for them, a delimiter at each `use`, and each stop marked with the handler it is for, so that one passes through a delimiter that isn't its own | | proposed |
| `seq` as a capturing handler, and the `#yield` rewrite deleted | [#2426](https://github.com/nanyx-lang/nanyx/issues/2426), [#2427](https://github.com/nanyx-lang/nanyx/issues/2427) | Planning |
| A frame that stops again as soon as it is resumed keeps its block | | proposed |
| Both on stack switching, behind `TaskLowering` | [#2164](https://github.com/nanyx-lang/nanyx/issues/2164) | Planning |

The drop and copy functions are the main new work. Without the first, a sequence abandoned partway leaks the frames
of every call it stopped in: the spike's tree walk, cut by `seq.take(4)`, left 15 blocks. Without the second, a step
pulled twice resumes a frame that is used up. A frame held once, as a sequence pulled once holds its steps, is resumed
in place without a copy, so [#2426](https://github.com/nanyx-lang/nanyx/issues/2426)'s "pulled once" case is found at
run time by the reference count, with nothing to prove.

# Open questions

- **A keyword for a plain call.** Whether `emit! x` becomes a keyword, with a keyword kind for a member that is called
  and returns.
- **How a member says it may capture.** A member that may capture makes every function that calls it a state machine,
  so the context's declaration has to say so: by the existing keyword-kind attribute, a new one, or the member's type.
  Then, how a capturing handler is written, `{ y, k -> ... }`, and how `k`'s type is written.
- **The contexts a generator's block needs.** Today a sequence's steps need their contexts where it is pulled
  (`[c] seq(a)`). In the spike, the block's contexts were needed where `generate` was called, since the backend
  couldn't adapt the block inside the step. The real version should carry them in the sequence's type, as `seq.map`
  does.
- **Named functions as handlers' callers.** A def that requires `Emit(int)`, passed as a value where
  `[Emit(int), c] a -> ()` is expected, is `NYX0205` at the call: a function named as a value requires its contexts
  where it is named. A visitor written as a named def would need `{ x -> visit(x) }`. Should a parameter whose type
  supplies a context accept it?
- **Code size.** Every function generic in contexts gets a state-machine copy where it is specialised for a capturing
  context, as `list.map` over `http.get` does today.
- **Continuations called more than once beyond sequences.** Copying frames would let a handler call `k` twice
  (backtracking, a search). Is that allowed, or only resuming a copy for a persistent sequence?
- **The functions that stop at the first value.** `list.selectOne` and `seq.selectOne` stop at the first `yield`, so
  a pushing handler can't serve them: they need a handler that never resumes, or stay lazy.
