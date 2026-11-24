---
title: async
description: Waiting, in direct style, with the Async context a host provides
order: 12.5
---

A function that can wait, on an HTTP request or a file, requires `Async`, and is otherwise an ordinary function:

```nanyx fragment
::dependency nanyx.http.core

import nanyx.http.core/http as http

def fetchName: [Async, Http] string -> string = { url ->
  def response = http.get(url) \orCrash("no name")
  response.body
}

println("Hello, {fetchName("https://example.com/name")}")
```

The console and HTTP hosts provide it, and their preludes bring it into every module of a program. A library that
names it in a type imports it from `nanyx/async`. A function whose type is inferred gets it from what it calls, and
a function that takes a lambda generic in its contexts, as `list.map` does, takes one that requires it with no second
version. How it is built, and why, is in [Async](../deep-dives/async), and what comes next in
[Concurrency and crash boundaries](../work-in-progress/concurrency-and-crash-boundaries).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Waiting, in direct style: a function that can wait requires [`Async`](#async), and is otherwise an ordinary
function. There is no `async` block and no `await` keyword: a call that waits, such as `http.get`,
suspends the program until it can carry on, and reads as a call that blocks.

Nothing starts `Async`: a host that can suspend provides it to the program's entry point, as it
provides `Console`, and inference adds it to every function that calls one that waits. A function
that doesn't require it provably never waits.

To do several things at once, start a [`Task`](#task) for each with `start!`, and [`Async.await`](#asyncawait) it: a task
runs at the same time as the code that started it, in the same instance, and can't outlive the def
that started it, which waits for its tasks before it returns. `start` and `await` are attached to
`Async`, so they are in scope wherever it is. How to use them is on the
[Async](../advanced/async) page.

## `Async`

```nanyx fragment
context Async = protected ()
```

The capability to wait. It has no members: the functions that wait, such as `http.get` and
`file.read`, require it, so their type says that they can. Only a host provides it: its prelude
exports `Async` and its `run` requires it, so the program's top level has it, and the host's `run`
passes it on to the entry point. It is protected, so no `use` outside this module can make one,
and a host that can't suspend doesn't provide it, so a program that needs it is an error there.

## `TaskScope`

```nanyx fragment
scoped context TaskScope = protected ()
```

The scope a task belongs to: the def that started it, as a `memory` block is the scope of its cells. Starting a
task requires it, and every module-level def whose body starts a task, and a script's top level, supplies it
through [`Async.concurrent`](#asyncconcurrent). It is scoped, so nothing that holds a task can leave the def that started it.

## `Task`

```nanyx fragment
type Task(a) = [TaskScope] protected (handle: I32)
```

A task: something running at the same time as the code that started it, in the same instance, which gives back
a value of type `a` when it finishes. [`Async.start`](#asyncstart) starts one and [`Async.await`](#asyncawait) waits for it. It belongs to the scope it
was started in, so it can't leave that def.

## `Async.concurrent`

```nanyx fragment
def Async.concurrent: [c] ([TaskScope, c] () -> a) -> a
```

Runs `block` as a scope of its own for the tasks it starts, and gives back its result once every
one of them has finished, awaited or not. The body of each module-level def that starts a task is
such a block, so a def is the scope of its tasks; a smaller one finishes, say, each batch before
the next starts: `for batches | batch -> concurrent({ batch \list.map { job -> start! run(job) } \list.map(await) })`.
Nothing that holds one of its tasks can leave it. It is a function, so its block is passed in
parentheses: `concurrent { ... }` would be a workflow's block.

## `Async.start`

```nanyx fragment
def Async.start: [Async, TaskScope, c] ([Async, c] () -> a) -> Task(a)
```

Starts a task that runs `work`, and gives it back at once: the task runs at the same time as the code that
started it, from the next time that code waits. It belongs to the innermost scope, the def that started it. It
is a `#delay` keyword: `start! e` is `start({ e })`, so `start! http.get(url) \orCrash("failed")` runs the
`orCrash` in the task.

## `Async.await`

```nanyx fragment
def Async.await: [Async, TaskScope] Task(a) -> a
```

Waits for `task` to finish, and gives back what it gave back. A task can be awaited more than once: each gives
back the same value.

## `Async.race`

```nanyx fragment
def Async.race: [Async, c] list([Async, c] () -> a) -> a
```

Runs each of `contestants` as a task, and gives back what the first to finish gave back, once
the others, which it cancels, have returned. A cancelled task carries on until it next waits:
then each call that waits gives back `#cancelled`, as `http.get` does, and a `sleep` returns at
once, so it returns soon, through its own error handling. A timeout is a race against a sleep:
`race([{ #some(http.get(url)) }, { sleep(5_000); #timedOut }])`. It crashes when `contestants`
is empty, which has no first.

<!-- End of the generated reference. -->