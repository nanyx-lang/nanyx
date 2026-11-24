---
title: "Async"
description: "How a function that can wait is typed and run, and why Nanyx waits in direct style"
order: 20
---

A function that can wait requires the [`Async`](../stdlib/async) context, which the console and HTTP hosts provide
([Hosts](../advanced/hosts#the-console-host)), and tasks do several things at once
([Async](../advanced/async)). This page says how they are built and why they have this shape. Async streams and crash
boundaries are not built yet; they are on
[Concurrency and crash boundaries](../work-in-progress/concurrency-and-crash-boundaries) in Work in progress.

# How it's built

- **A context with no members.** `nanyx/async` declares `Async` as `protected ()`, so no `use` outside that module can
  make one, and a function that requires it is passed nothing. A host that can suspend provides it: its prelude
  re-exports it and its `run` requires it and passes it to the entry. Inference adds it to every function that calls
  one that waits, as it adds `Console`. How the compiler makes a memberless context ambient is on
  [Hosts](./hosts#how-its-built).
- **A host that can't suspend doesn't provide it,** so a program that needs `Async` is an error naming the host
  (`NYX0205`), as for any context a host doesn't provide.
- **A wait holds only its task.** On the console and HTTP hosts, a request or a sleep stops the task that waits while
  it is in flight, and the instance's other tasks run (below): on the runtime's `nanyx:async` wait in a core module,
  and on one waitable set the instance shares in a component ([Components](./components#waiting-while-other-tasks-run)).
  A wait that is a blocking host call holds the instance's thread until it is done: a file's, since WASI preview 1 has
  no file functions that don't block, and the component roots read and write files in the same way. Each instance
  runs on a thread of its own: the console host has one; the HTTP host runs each request's instance on a thread of its
  own (`WasmServer.serve`), so a handler holds only its own, and the host answers other requests meanwhile.
- **The backend knows which functions can wait.** `Async` has no members, so nothing a function is passed says it
  requires it. A function's IR type says so instead (`IrFunction`'s `waits`, in `CoreIr.fs`), from the contexts its
  type requires (`CoreIrLowerTypes.toIrTypeIn`). A function generic in contexts is specialised on what its context
  variables stand for, so it is compiled apart for a function that can wait and for one that can't: `list.map` over
  `http.get` and `list.map` over a pure lambda are two functions, and only the first is a state machine (below).

# How tasks are compiled

Tasks ([#1890](https://github.com/nanyx-lang/nanyx/issues/1890)) need a function that waits to stop where it waits and
carry on later, while other code runs on the same stack. The backend compiles such a function to a state machine, as
C# and Rust compile async functions. `TaskLowering` decides how; it is the one place to change for another lowering,
such as WebAssembly's stack switching once it is widely supported
([#2164](https://github.com/nanyx-lang/nanyx/issues/2164)). A function that can wait stops only where something it waits
for hasn't finished: a task, a scope's tasks, or the host (below); a call that could stop and doesn't costs a check.

- **Split points.** A function is a state machine when it can stop: when it calls a function value whose type waits,
  waits for a task or for a scope's tasks (below), or calls a function that is a state machine itself (`CoreIrSplitPoints.stateMachines`, over the module once it is counted).
  Each such call is a split point, numbered from 1 in the order the code is written, so the states inside any
  expression are a range. The start function and the getter of a cached value run state machines without being one
  (below). A function that can't stop is compiled as before.
- **Stopping.** A call that stops sets `__nanyx_suspending`, leaves its frame in `__nanyx_suspended`, and returns a value
  no one reads. After each split point the caller checks the flag. When it is set, the caller copies the variables it
  keeps there into a frame of its own, on the heap: those in scope that the code after it reads, which counting's own
  reads (what it drops or reuses later) are among. With them go the table slot of its resume entry, its state and the
  callee's frame. Then it returns the same way. Nothing is allocated unless a call does stop, and a call that finishes
  costs one `global.get` and a branch. A tail call that can stop stays a tail call: the callee's frame takes the
  caller's place.
- **Carrying on.** A state machine's resume entry, `<name>__resume`, passes its frame in `__nanyx_resuming` and calls the
  function, whose first code takes the frame back: it restores the variables, frees the frame, and goes through its own
  code to the split point it stopped at, skipping what ran already. Each link of a chain before the one that holds the
  state runs only when the function is not resuming or the state is in it; an `if`, a `match` or a loop resuming in a
  branch takes that branch, without testing its condition or binding its pattern again; and counting before a body
  that holds the state is skipped. At the split point, it calls the resume entry of the callee's frame instead of the
  callee. So resuming a task resumes each function it stopped in, outermost first, as Rust polls a future: the code is
  structured as before, with no jump table, and a crash's stack still shows each function.
- **Temporaries.** A value the emitter would compute where it is used, on the stack (see `CoreIrEmit`), can't be there
  when the function stops: a temporary whose value holds a split point, and those waiting to be used while it is
  computed, are kept in locals instead (`SplitPlan.StoredTemps`). Each is computed where it would have been used: after
  the counting between its definition and its use, which the lowering puts before it, as in `f(x, x.length)`, where the
  count `x` takes for the first argument comes before `x.length` gives up the reference the code held.
- **Running one to the end.** Code that calls a state machine without being one, the start function, the getter of a
  cached value and a component's export, runs it to the end (`TaskLowering.blockOn`): while it stops, the scheduler
  runs the other tasks until its turn comes again (below), and then resumes it.
- **Testing it.** A wait stops only when what it waits for isn't done, so `WasmOptions.YieldAtEveryWait` (`nanyx run
  --yield-at-every-wait`, for working on the compiler) makes every call of a function value that can wait stop once
  before it is made, through `__nanyx_yield`, which has a frame of its own. The tests run programs that wait both ways, with memory checks, and
  compare what they print: `RunStateMachineTests.fs`, and every test of the HTTP client.

# Why it's built this way

## Direct style

There are three ways to give a language async.

**A. A workflow:**

```nanyx fragment
def greeting = async {
  def name = await! getName()
  return "Hello, {name}"
}
```

It needs no runtime support: the block is rewritten into continuations. But every function that waits must be
written as a workflow and every caller must `await!` it, which is the "coloured function" problem of C#, JavaScript
and Rust. A `list.map` over an async function needs an async `map`. And each `await!` allocates its continuation.

**B. Direct style, with the waiting in the type as a context.** A function that can wait requires `Async`, and is
otherwise an ordinary function:

```nanyx fragment
def greeting: [Async, Http] () -> string = {
  def response = http.get("https://example.com/name") \orCrash("no name")
  "Hello, {response.text}"
}
```

There's no `await`: a call that waits suspends the caller until it can carry on, as a blocking call reads. The colour
is still in the type, as a context, but contexts are already polymorphic: `list.map` takes a `[c] a -> b`, so it maps
an `[Async]` function with no second version. This is the model of OCaml 5's effects, Go, Java's virtual threads, and
Flix and Unison's effect systems.

**C. Every call can block, and nothing says so.** Go's model without the type. Simplest to write, but it loses what
contexts are for: a function's type no longer says whether it can wait, and a host that can't suspend has no way to
refuse a program that would.

Nanyx takes **B**, with no visible `await` at a suspension point. It keeps capabilities in types, avoids colouring
through context polymorphism, and leaves workflows for what they are good at (generators, parsers).

## What colouring costs

A and B both put "this can wait" in the type; the difference is who pays for it.

- **With A,** every generic function pays. `list.map(urls, fetch)` gives a `list(Task(Response))`, not the responses,
  so the library needs a second set of functions for tasks (`async.traverse`, an async `fold`, an async `filter`),
  the second standard library C# and Rust live with. Every function between the entry and the network call is written
  in the workflow too.
- **With B,** the prelude's higher-order functions are already generic in contexts (`[c] a -> b`), so
  `urls \map { url -> http.get(url) }` works, and is `[Async]` because its lambda is.
- **A function stored in data, in both.** A record field typed `string -> int` can hold neither A's
  `string -> Task(int)` nor B's `[Async] string -> int`. What differs is the fix. In A the field is made generic in
  its result, `type Handler(r) = (run: string -> r)`, and whoever calls `run` gets a `Task(int)` from one handler and
  an `int` from another, so every consumer is written twice. In B the field is made generic in contexts,
  `type Handler = [k] (run: [k] string -> int)`, as `seq`'s pull function is; whoever calls `run` gets an `int`
  either way, and only needs `k` in scope. B's extra demand is that library authors know `[k]`-generic types, which
  are less familiar than a type parameter.
- **At run time,** `Async` has no members, so B passes nothing for it, while A allocates a closure per `await!`, which
  in-place reuse can't remove. What tasks will cost is on
  [How it runs](../work-in-progress/concurrency-and-crash-boundaries#how-it-runs).

So B is not free of colour. Its colour is a context, and contexts already have the polymorphism that makes colours
cheap.

## Where `Async` comes from

Nothing starts it: like `Console`, the host provides it.

- **No `async { }` block.** "May wait" is the `Async` context, which code has or doesn't. "Runs at the same time" is a
  separate thing, a task ([Structured concurrency](../work-in-progress/concurrency-and-crash-boundaries#structured-concurrency)).
  Those are the two things an `async` block mixes up in C# or JavaScript.
- **Code without `Async` can't get it.** A library that doesn't ask for it provably never waits, as one that doesn't
  ask for `Console` never prints.
- **No `runBlocking`.** Inside a host that can suspend there's nothing to bridge, and in one that can't, a blocking
  runner would need an event loop the host doesn't have. The missing-context error is the honest answer.

# Tasks and scopes

`start!` starts a task, `await` waits for one, and each module-level def is the scope of the tasks it starts
([Async](../advanced/async)).

- **The library.** `nanyx/async` declares them on six intrinsics (`Intrinsics.taskStart` and the others, in
  `Builtins.fs`), and `nanyx/host.async` a host's waits on a seventh, `Intrinsics.hostWait` (below). `TaskScope` is a scoped context, as `Memory` is, and `Task(a)` is a type in its scope,
  `[TaskScope] protected (handle: I32)`. `Async.concurrent` supplies `TaskScope` to its block, as `memory` supplies `Memory`: it
  opens a scope, runs the block, and waits for the scope's tasks before it gives back the block's result. `start`
  requires `TaskScope` and starts a task whose function runs `work` and keeps what it gives back as a function of
  nothing, `{ value }`, so that the scheduler can drop it whatever its type, and each `await` gets a reference of its own
  by calling it. `await` requires `TaskScope` too, so a function that awaits a task it is given borrows its caller's
  scope rather than supplying one, which a function that takes a `Task` and doesn't require it would.
- **`start!`.** `start` and `await` are defs attached to `Async`, which has no members, `Async.start` and
  `Async.await`, in scope by their bare names wherever `Async` is (`AttachedContextDefs`). A `#delay` keyword rewrites
  nothing but itself, so `Attributes.applyAttributes` allows `@customKeywordType(#delay)` on a def attached to a context
  the module declares, and `Attributes.keywordKinds` reads it there by the def's bare name (any other kind needs a
  builder, and a def attached to no context can't be a keyword, `NYX0019`). The hosts' preludes export `Async` alone.
- **Each def a scope.** `TaskScopes.wrap`, before inference, makes the body of each module-level def that names
  `start` where nothing in it binds the name (a parameter or a local def named `start` is not the one that starts a
  task), and a script's entry point if it does, the block of a call of `Async.concurrent`; in a module that defines a
  `start` of its own, only `Async.start` makes one (`TaskScopeSyntax.scopeCall`, with
  `nanyx/async` imported under an alias no code can write). So the rules that keep what requires a scoped context in
  its call (`ScopedCalls`) keep a def's tasks in it: a `Task` given back is `NYX0233`, with a message about tasks
  rather than the call. A lambda requires its contexts where it is called, so nothing ties a lambda that starts a task
  to the def it is written in; `TaskCaptures.escapeErrors` reports a def that is a scope whose result holds one. Local
  defs and lambdas are not scopes: their tasks belong to the def they are written in. A module-level def that writes no
  type, isn't a scope, and is inferred to require `TaskScope` starts a task through another name (`def go =
  Async.start`); `TaskCaptures.unscopedErrors` reports it (`NYX0211`) at its first call whose callee requires
  `TaskScope`, and a script that calls it is then not told its entry point doesn't fit the host's `run`.
- **Tasks share only values.** A function that uses a cell or a reference requires its `memory` block's `Memory`, and
  a task's function must not (`NYX0279`). The check is on types, not names (`TaskWork`): where inference names
  `Async.start` or `Async.race`, the context variables of the functions its type takes are _work variables_; a def
  generalised over what a work variable came to stand for has work variables of its own, so `def spawn = { f -> start(f) }`
  and `def go = start` start tasks as far as the check is concerned, at each place they are named. Once the module is
  inferred, `TaskCaptures.captureErrors` reports each place where a work variable stands for contexts with a `Memory`,
  at the call's argument.
- **The scheduler** (`WasmScheduler`) runs an instance's tasks on its one stack, as state machines. A task is a block
  of the function it runs, the frame it stopped in, its scope and its own innermost scope, the function that gives its
  result, and the regions of the `memory` blocks it is in, which the scheduler swaps in when it runs it. The queue is
  first in, first out, so the order tasks run in is fixed: a task starts from the next time the code that started it
  waits. `taskWait` and `scopeExit` are split points that stop the running task while what they wait for hasn't
  finished, with a frame of their own whose resume entry looks again. Each task has a scope for the tasks it starts
  directly, and finishes once they have. The main task, the program's own code, is made when something first needs
  it, and freed once it holds no scope.
- **Waits on the host.** A host's code that waits starts what it waits for and polls it, calling `waitOnHost` between
  polls ([Hosts](../advanced/hosts#waiting-on-the-host)). Its intrinsic, `hostWait`, is a split point that keeps the
  host's wait it is given, a function value of a timeout, and the shortest timeout given, and stops until the
  scheduler has made that wait once more (`__nanyx_host_round`). A task resumed while what it waits for hasn't
  happened has stalled (`__nanyx_task_stalled`); the scheduler counts the tasks that stall in a row, and when every
  task in the queue has (`__nanyx_task_idle`), nothing can happen until the host does something, so it calls the
  host's wait, if a task gave one. The tasks that waited on the host then poll again. On the hosts' core roots, the
  wait is the runtime's `nanyx:async` `wait`, and a request is `nanyx:http`'s `start` and `poll`
  ([Foreign functions](./foreign-functions)). A sleep, `Clock`'s `sleep`, waits on the host for what is left of it
  until the monotonic clock has passed its deadline. Passing the wait, rather than marking it for the compiler to
  find, keeps it a function like any other: nothing that only the scheduler calls has to survive linking, and a host
  can write its wait in Nanyx.
  The playground host writes its own: its wait, `pageWait`, spins on the page's clock for the timeout, since a page
  has nothing that finishes on its own, and its `sleep` waits on it, so a program's tasks take turns while one sleeps.
- **Cancellation.** A task has a cancelled flag (`Intrinsics.taskCancel` sets it on the task and on every task in each
  of its scopes, and theirs in turn; a task a cancelled task starts is cancelled from the start). Nothing stops a task:
  a host's code that waits looks at it (`nanyx/host.async`'s `cancelled`) and gives back `#cancelled`, or, for a sleep,
  returns. A task waiting on the host that is cancelled meanwhile carries on once at once, without the host's wait: the
  frame of its wait keeps whether the task was cancelled when it stopped, and its resume entry stops again only while
  that is unchanged and the host hasn't waited, so it waits again for real if its code waits again. `race` is
  `nanyx/async`'s: in a scope of its own (`Async.concurrent`), it starts each contestant, waits until one has given back its value
  (`Intrinsics.scopeWaitAny`, a split point), cancels the others, and awaits the winner; the scope waits for the losers.
  The contestants may need different contexts: a list's elements that are functions join as a function that may need
  them all (`BranchJoin.joinFunctions`).
- **A crash in a task** traps, as any crash does, which ends the instance and its other tasks.

## Why tasks are built this way

- **A task nobody awaits is waited for,** as in Kotlin, not cancelled, as in Swift: a `start!` whose result is never
  used is probably started for what it does, and its effect was wanted.
  One whose result isn't `()` is more likely a forgotten `await`, so the default linter warns about it
  (`UnawaitedTasks`, in `LinterTaskRules.fs`, as `unawaitedTask`), and `unusedResults` leaves every task to it.
- **`concurrent` is a function,** `Async.concurrent`, the same one that makes each def a scope, so a scope smaller than
  a def needs nothing of its own in the compiler.
- **`await` is a function, not a keyword.** A keyword exists to capture the rest of a block; waiting on a task is an
  ordinary call that suspends, like `http.get`, so it's written like one.
- **A task never stands in for its value.** `a` is a `Task(Response)` until it's awaited. Having it awaited when it's
  first read would be an implicit conversion, which Nanyx doesn't have.
- **`Task`, not `Process`.** A task shares the instance and lives in a scope; a process, when there are processes, is
  isolated and runs on its own. The names keep the two levels apart.
- **The scope is the def, not the nearest lambda,** so that `urls \map { url -> start! http.get(url) }` starts every
  request at once: the def, not `map`'s lambda, waits for them.
