---
title: "Async"
description: "Waiting in direct style, and doing several things at once with tasks"
order: 5.5
---

A function that can wait, such as `http.get` or `file.read`, requires the `Async` context. It is otherwise an ordinary
function: there is no `async` block and no `await` keyword at the point where it waits, and a call that waits reads as
one that blocks. The console, HTTP and playground hosts provide `Async`; inference adds it to every function that calls one that
waits, as it adds `Console`. Why it is built this way is on [Async](../deep-dives/async) in Deep dives.

# Tasks

To do several things at once, start a task for each. `start!` runs the expression after it as a task, and gives back a
`Task` at once; `await` waits for the task to finish, and gives back what it gave back:

```nanyx
def both = { _ ->
  def a = start! 20 + 1
  def b = start! "answer"
  "{await(b)}: {await(a) * 2}"
}

println(both())
-- answer: 42
```

`start!` takes the whole expression after it, so in `start! http.get(url) \orCrash("failed")` the `orCrash` runs in
the task, not on the `Task`. A task can be awaited more than once, and each `await` gives back the same value.

Starting a task in a lambda starts one each time the lambda runs, so `map` starts a task for each element, and they all
run before the first is awaited:

```nanyx
def squares = { (xs: list(int)) ->
  xs \list.map { x -> start! x * x } \list.map(await)
}

println(squares([1, 2, 3]))
-- [1, 4, 9]
```

`start` and `await` are [attached to `Async`](./attached-definitions#attached-to-a-context), so they are in scope
wherever `Async` is. `start` is a [`#delay` keyword](./workflows#keyword-kinds), so `start! e` is `start({ e })`. A
name of your own wins over them: a local def or a parameter named `start` is yours.

# Each def is a scope

A task can't outlive the def that started it. A module-level def whose body starts a task, directly or in a lambda
written in it, doesn't return until every task it started has finished, awaited or not; at a script's top level, the
program doesn't end until they have:

```nanyx
def notify = { _ ->
  start! println("the task runs")
  println("the def returns")
  42
}

println(notify())
-- the def returns
-- the task runs
-- 42
```

A task nobody awaits is waited for, not cancelled: one started for what it does, such as writing a log, does it.
The linter warns about one that gives back something other than `()`, since nothing reads it
([`unawaitedTask`](../standard-tools/official-linter#task-that-is-never-awaited)).

Nothing that holds a task can leave its def: giving back a `Task`, or a lambda that starts one, is an error
([NYX0233](../standard-tools/errors#nyx0233)), as a lambda over a cell can't leave its [`memory`](./memory) block. The
scope is the def, not the nearest lambda: a task started in a local def or a lambda belongs to the def it is written
in. A function that awaits a task it is given borrows its caller's scope.

A def that is a scope and is called last by another, or by itself, joins the caller's scope rather than opening its own,
so the call stays a [tail call](../tour/functions#tail-recursion), and a def that starts a task in each turn and calls
itself last is a loop. Every turn's tasks belong to the outermost call's scope: one nobody awaits is waited for when that
call returns, at the loop's end, not at the end of its turn. A scope keeps only its tasks still running, so a loop that
awaits the task of each turn holds the same memory however many turns it runs. Both defs must be in the same module. A
def that writes no type joins its caller's scope only when it is called last by a def that does so too, such as itself
or one that writes its type; it is then two defs, one opening the scope and one running its body, so a task given back
from it is reported at its value rather than where the task is started.

```nanyx
def total: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else total(n - 1, acc + await(start! n))
}

println(total(100_000, 0))
-- 5000050000
```

A def is a scope when its body names `start` (or `Async.start`), so a def that starts a task only through another
name, as `go(...)` after `def go = Async.start` does, is not one, and starting the task there is an error
([NYX0211](../standard-tools/errors#nyx0211)): name `start` in the def itself.

# A smaller scope

`concurrent` runs a block as a scope of its own, and gives back what the block gives back once every task started in it
has finished. Use it to finish one batch of work before the next starts:

```nanyx
def batches = { _ ->
  for [[1, 2], [3, 4]] | batch ->
    concurrent({ for batch | x -> start! println("job {x}") })
    println("batch done")
}

batches()
-- job 1
-- job 2
-- batch done
-- job 3
-- job 4
-- batch done
```

It is a function, so its block is passed in parentheses: `concurrent { ... }` would be a
[workflow's block](./workflows). Like `start`, it is attached to `Async`.

A smaller scope may await a task of the def's scope, which outlives it:

```nanyx
def outer = { _ ->
  def t = start! 20
  def inner = concurrent({ await(t) + await(start! 1) })
  inner + 1
}

println(outer(()))
-- 22
```

# Tasks share only values

A task can't use what belongs to a `memory` block: starting one with a function that uses a cell or a reference
(`MRef`, `MList`) is an error ([NYX0279](../standard-tools/errors#nyx0279)), whether `start` starts it, `race` does, or a
function that passes on what it is given to either. Values are immutable, so a task can never
change what another holds; give a task what a cell holds, and change the cell with what the task gives back.

# How tasks take turns

The tasks of a program run on one thread, one at a time. A task runs from the first time the code that started it
waits, and each runs until it waits itself, when the next takes its turn, first started first. A task that never waits
runs to the end before any other runs.

A request, or a [`sleep`](../stdlib/clock), lets the other tasks run while it waits, so requests started as tasks
are in flight at once, and take about as long together as the slowest of them:

```nanyx fragment
def pages: [Async, Http] list(string) -> list(string) = { urls ->
  urls \list.map { url -> start! http.get(url) } \list.map { task -> (await(task) \orCrash("failed")).body }
}
```

A file's functions still hold the thread until they are done: the console host reads and writes files with WASI
preview 1, which has no way to start a read and come back for it, and a program built as a
[component](./hosts#built-as-a-component) reads and writes them in the same way, though its requests and sleeps let
the other tasks run.

A crash in a task ends the program, the other tasks too, as any crash does.

# Racing

`race` runs each of the functions it is given as a task, and gives back what the first to finish gave back. It cancels
the others, and waits for them to return before it does:

```nanyx
def fastest = { _ ->
  race([{ sleep(50); "the quick one" }, { sleep(60_000); "the slow one" }])
}

println(fastest())
-- the quick one
```

The functions may each need different contexts: the list is of functions that may need any of them. `race`, like
`start` and `await`, is attached to `Async`, so it is wherever `Async` is.

A timeout is a race against a sleep:

```nanyx fragment
def limited = { url: string -> race([{ #some(http.get(url)) }, { sleep(5_000); #timedOut }]) }
```

# Cancelling

There is nothing to throw, so a cancelled task stops through its own error handling: each call that waits gives back
`#cancelled`, a case of [`HttpError`](../stdlib/client#httperror) and of [`FsError`](../stdlib/filesystem#fserror), and a `sleep` returns at once.
A request in flight stops waiting as soon as its task is cancelled; a file's function in a cancelled task does nothing.
A task that never waits can't be cancelled until it does.

```nanyx fragment
match http.get(url)
  | #some(response) -> response.body
  | #cancelled -> ""  -- only once the task was cancelled; usually passed on with `try`
  | e -> crash("failed: {e}")
```

Cancelling a task cancels every task it started, and every task those started, so a cancelled task finishes promptly,
and `await` never reports cancellation: the task being awaited was started by the one awaiting it, and is cancelled
with it. Today `race` is what cancels a task. A program built as a component finishes a request, or a sleep, that was
already under way when its task was cancelled, before it gives back `#cancelled`, or returns.
