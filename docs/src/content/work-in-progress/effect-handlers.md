---
title: "Proposal: Effect handlers"
description: "Contexts whose members a handler runs at once or captures: Emit's pushing builders, capturing members marked @resumes(#once | #many), and what a spike measured"
order: 7
---

This page is about effect operations in the sense of Koka: calls to a member of a context whose handler, the
instance a `use` loads, runs the code after the call at once (a pushing, or tail-resumptive, operation) or captures it
to run later. It is the "effect handlers in general" question on
[Concurrency and crash boundaries](./concurrency-and-crash-boundaries#open-questions). Two cases are in hand:

- **Pushing: `Emit(a)`.** Decided on 2026-11-24 and being built
  ([#2566](https://github.com/nanyx-lang/nanyx/issues/2566)): the builders push through a new prelude context.
- **Capturing: members marked `@resumes`.** Designed with the maintainer on 2026-11-24, not started
  ([below](#capturing-members-that-take-the-rest-of-the-block)): a member may capture the rest of the block, resumed
  once or many times, on async's state machines. `yield` is the first such member
  ([#2426](https://github.com/nanyx-lang/nanyx/issues/2426), with what is left of
  [#2427](https://github.com/nanyx-lang/nanyx/issues/2427)).

Contexts are already handlers of the first kind: a member is passed to the functions that require it, as Koka passes
its handlers ("evidence passing"), and it runs on top of the caller's stack and returns. What capturing adds is the
continuation, `k`: the rest of the handled code, from just after the call up to the `use` that loaded the handler.
What a handler can do with `k` is a ladder, each rung needing more of the compiler:

| What the handler does with `k` | For | Needs |
|---|---|---|
| Calls it once, as its last act | `Console`, `Log`, `Emit`, any member today | nothing: it is a plain call |
| Never calls it | early exit, `list.selectOne` | leaving the handled code, and dropping what it held |
| Calls it once, later | lazy sequences, tasks, coroutines | the rest of the code kept somewhere else than the stack |
| Calls it more than once | sequences pulled twice, backtracking, every answer to a search | that, copied |

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

# Capturing: members that take the rest of the block

Designed with the maintainer on 2026-11-24. Nothing of it is built. `yield` is its first user; the language's other
capturing operations, `Async`'s waits, are hard-wired today and become its second.

## Declaring one

A member that captures says so with an attribute, and says how many times its continuation may be resumed:

```nanyx fragment
export context Yield(a) = (
  @resumes(#many)
  yield: a -> ()
)

context Coroutine = (
  @resumes(#once)
  pause: () -> ()
)
```

- **No attribute:** a plain member, as every member is today. It runs and returns, and costs a call.
- **`@resumes(#once)`:** the handler is given `k`, and may resume it once or not at all. Resuming it a second time is a
  crash, with its stack.
- **`@resumes(#many)`:** `k` may be resumed any number of times, within the rule [below](#many-and-scoped-contexts).

It is per member, not per context, so a context can mix them: a `Choose` with a capturing `choose` and a plain one.
The attribute is a union case, as `@customKeywordType` is, so it needs no grammar, and could become a keyword before the
member later.

## Calling one

Code that calls a capturing member is written as it is today: `walk: [Yield(int)] Tree -> ()` calls `yield(x)` as it
would call any member, and inference adds `Yield(int)` to its type as it adds `Console`. There is no `await` and no new
function type. A function that requires a context with a capturing member is compiled to a state machine, as one that
requires `Async` is ([Async](../deep-dives/async#how-tasks-are-compiled)), and a call of the member is a split point.
Whether a function is a state machine is read from its type after inference, as `IrFunction`'s `waits` is.
Context-generic code is unchanged: `list.map` over a function that yields is `list.map` specialised with
`c = Yield(int)`, as `list.map` over `http.get` is today.

## Handling one

At a `use`, a capturing member's implementation takes the member's parameters and then `k`. `seq` becomes a library
function:

```nanyx fragment
export def seq: ([Yield(a), c] () -> ()) -> [c] seq(a) = { body ->
  seq.fromStep {
    use Yield(a)(yield = { x, k -> #next(x, seq.fromStep(k)) }) in
      body()
      #done
  }
}
```

- **`k` is a function, of an ordinary function type.** It takes what the member gives back, `()` here, and gives what
  the whole `use ... in` gives, its answer type, here a sequence's step. It requires the contexts the rest of the
  handled code needs besides the one handled, `[c]` here, so a sequence's steps need them where it is pulled, as
  `[c] seq(a)` says today.
- **No return clause.** Koka's handlers have one, to turn the body's value into the handler's; in Nanyx that is the last
  line of the `in` block, `#done` above.
- **Deep.** When `k` runs the rest of the block and that calls `yield` again, the same `use` handles it, as its scope
  says.
- **Not resuming** is not calling `k`: `list.selectOne` is a handler that keeps the first value and drops `k`. Dropping
  `k` gives up what its frames hold, as dropping any value does.
- **The handler an operation reaches** is the innermost `use` of its context, as for any member. A stop goes up
  through the frames of every function between the call and its `use`, and through any `use` of another context
  (a wait inside a sequence, a sequence inside a task); each stop is marked with the handler it is for.

## Once and many

A continuation resumed more than once runs the rest of the code twice from the same point, so the frames saved at the
capture must outlive the first run. On the state machines, resuming `k` runs its frames in place when the reference
count says nothing else holds it, and copies them first when something does. So a sequence pulled once never copies,
with nothing to prove, and a step pulled twice costs one copy of a few frames. Each state of a state machine compiled
for a `#many` member gets a copy function beside its drop function; one compiled only for `#once` members needs only
the drop function.

Why both, and not one:

- **Sequences are pure.** Pulling a step twice gives the same answer, and runs its effects again
  ([Lazy sequences](../deep-dives/lazy-sequences)). Today's rewrite gets that for free, since the rest of the block is
  a lambda. Keeping `yield` that way needs `#many`; making it `#once` would mean storing each step when it is first
  pulled, which changes what a sequence is to suit how it is compiled.
- **Tasks are one-shot.** Nothing resumes a wait twice. WebAssembly's stack switching gives only one-shot continuations:
  the proposal's `resume` consumes its continuation, a second `resume` traps, and there is no instruction to copy one
  ([Explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md), read
  2026-11-24). So `Async`'s waits are `#once`, and moving tasks onto stacks
  ([#2164](https://github.com/nanyx-lang/nanyx/issues/2164)) changes no promise the language makes. A `#many` member
  stays on the state machines whatever the host.
- **The type says which.** A reader who sees `[Choose]` in a function's type can find that its code may run more than
  once.

## `#many` and scoped contexts

What starts once and ends once can't be resumed twice: a `memory` block would end its region twice, and a scope would
await its tasks twice. Those are the scoped contexts, `Memory` and `TaskScope`. So a `#many` member can't capture
across one: where the block of a function that supplies a scoped context (`memory`, `Async.concurrent`) would require
a context with a `#many` member, it is an error at the call, naming the block. It is the check `ScopedCalls` already
makes, from the other side.

```nanyx fragment
def bad: [Yield(int)] () -> () = {
  memory {
    mut n = 0
    yield(n)  -- error: yield's continuation may be resumed more than once, so it can't cross this block's end
    n := 1
  }
}

def fine: [Yield(int)] () -> () = {
  def total = memory {
    mut n = 0
    n := 5
    n
  }
  yield(total)
}
```

A `#once` member may capture across them; its continuation then keeps the regions of the blocks it is in, as a task
keeps those of its own today ([Async](../deep-dives/async#tasks-and-scopes)).

## What `yield` becomes

- **A lambda with `yield` in it is not a sequence by itself:** it requires `Yield(a)`, and `seq { }`, a library
  function, makes it a sequence. Builders that took a lambda that yields take one that emits
  ([#2566](https://github.com/nanyx-lang/nanyx/issues/2566)).
- **It works from any depth,** so a recursive walk yields from inside its recursion.
- **The rewrite's limits go:** a `yield` in a `while` loop, and a `return` in a sequence's block.
- **The `#yield` keyword kind goes,** with its hard-wired calls to `seq.cons`, `__seqLast` and the rest.

## Diagnostics

- A `#once` continuation resumed twice: a crash at run time, with its stack.
- A `#many` capture across a scoped context: a compile error at the call.
- A capturing member given without `k`, or a plain one given a `k`: a compile error at the `use`.
- `@resumes` on anything but a member of a `context` declaration: a compile error at the attribute.

## Stack switching

What stack switching would change is how a `#once` capture is compiled, not what it means: a task, or a coroutine,
runs on a stack of its own, and a capture is a switch. Every function is then compiled once, with no check after its
calls and no state-machine copy of each function generic in contexts, and a deep recursion that waits costs one
switch, not a frame saved for each level. It won't serve `#many`, and a stack for each small sequence costs more than
its few frames, so the state machines stay, and a backend chooses per host behind `TaskLowering`. As of 2026-11-24
the proposal is at phase 3; Wasmtime's implementation is experimental, on x86-64 only, and off by default in its C
API, which the runtime's .NET package builds on; and no browser ships it without a flag. Running .NET host functions
on a stack Wasmtime switched to may hit the problem that stopped
[#1901](https://github.com/nanyx-lang/nanyx/issues/1901). That stays [#2164](https://github.com/nanyx-lang/nanyx/issues/2164).

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
| A frame can be dropped: a drop function for each state, beside its resume entry, from the types the emitter knows where it saves the variables | | proposed |
| `@resumes(#once \| #many)`: split points for capturing members, a delimiter at each `use`, `k` given to the handler, each stop marked with the handler it is for, and the scoped-context rule | | proposed |
| A frame can be copied, for `#many`: a copy function for each state, and a resume that copies only when the frames are shared | | proposed |
| `seq` as a handler of `Yield`, and the `#yield` rewrite deleted | [#2426](https://github.com/nanyx-lang/nanyx/issues/2426), [#2427](https://github.com/nanyx-lang/nanyx/issues/2427) | Planning |
| A frame that stops again as soon as it is resumed keeps its block | | proposed |
| `#once` captures, tasks first, on stack switching, behind `TaskLowering` | [#2164](https://github.com/nanyx-lang/nanyx/issues/2164) | Planning |

The drop functions come first: without them a sequence abandoned partway leaks the frames of every call it stopped in
(the spike's tree walk, cut by `seq.take(4)`, left 15 blocks), and a handler that never resumes can't be written.

# Open questions

- **A keyword for a plain call.** Whether `emit! x` becomes a keyword, with a keyword kind for a member that is called
  and returns. The same kind would keep `yield x` written without brackets once it is a member.
- **`defer` and `#many`.** `defer` is a `#bindF` keyword, not a scoped context, so the rule above doesn't reach it, but
  a continuation resumed twice would run its cleanup twice.
- **Mutable cells and `#many`.** A cell kept in a WebAssembly local is copied with its frame, and a captured cell's box
  is shared, so two resumptions would see them differently. Today a cell can't outlive its `memory` block, which the
  rule above keeps a `#many` capture inside; check there is no other way to hold one across a capture.
- **Which handler a lambda's operations reach.** A lambda requires its contexts where it is called. If a higher-order
  function loaded its own handler of a context and called a lambda that requires it, the lambda's operations must
  reach the caller's handler, not the function's (Koka's "accidental handling"). Check how specialisation decides it.
- **Named functions as handlers' callers.** A def that requires `Emit(int)`, passed as a value where
  `[Emit(int), c] a -> ()` is expected, is `NYX0205` at the call: a function named as a value requires its contexts
  where it is named. A visitor written as a named def would need `{ x -> visit(x) }`. Should a parameter whose type
  supplies a context accept it?
- **Code size.** Every function generic in contexts gets a state-machine copy where it is specialised for a capturing
  context, as `list.map` over `http.get` does today.
- **Users' own capturing handlers of `Async`.** Whether a library may write a scheduler. `#once` allows it; nothing
  needs it yet.
