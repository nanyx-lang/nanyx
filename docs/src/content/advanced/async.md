---
title: "Async"
description: "Waiting in direct style, and doing several things at once with tasks"
order: 5.5
---

A function that can wait, such as `http.get` or `file.read`, requires the `Async` context. It is otherwise an ordinary
function: there is no `async` block and no `await` keyword at the point where it waits, and a call that waits reads as
one that blocks. The console and HTTP hosts provide `Async`; inference adds it to every function that calls one that
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

`start!` and `await` are in the host's prelude, beside `Async`: `start` is a function of `nanyx/async` that is a
[`#delay` keyword](./workflows#keyword-kinds), so `start! e` is `start({ e })`.

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

Nothing that holds a task can leave its def: giving back a `Task`, or a lambda that starts one, is an error
([NYX0233](../standard-tools/errors#nyx0233)), as a lambda over a cell can't leave its [`memory`](./memory) block. The
scope is the def, not the nearest lambda: a task started in a local def or a lambda belongs to the def it is written
in. A function that awaits a task it is given borrows its caller's scope.

# Tasks share only values

A task can't use what belongs to a `memory` block: starting one with a function that uses a cell or a reference
(`MRef`, `MList`) is an error ([NYX0279](../standard-tools/errors#nyx0279)). Values are immutable, so a task can never
change what another holds; give a task what a cell holds, and change the cell with what the task gives back.

# How tasks take turns

The tasks of a program run on one thread, one at a time. A task runs from the first time the code that started it
waits, and each runs until it waits itself, when the next takes its turn, first started first. A task that never waits
runs to the end before any other runs.

Today a call that waits still holds the thread until it is done, so tasks take turns only where they wait for each
other or for their def's scope; waiting on the network or a timer while other tasks run is what comes next, with
`sleep`, cancellation and `race` ([Concurrency and crash boundaries](../work-in-progress/concurrency-and-crash-boundaries#plan)).

A crash in a task ends the program, the other tasks too, as any crash does.
