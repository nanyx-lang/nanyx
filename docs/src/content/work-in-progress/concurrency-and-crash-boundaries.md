---
title: "Concurrency and crash boundaries"
description: "Async, tasks, isolated processes, and keeping a crash from taking the whole program down"
order: 6
---

The `Async` context is built: a function that can wait requires it, and waits in direct style. How it is built, and
why direct style, is on [Async](../deep-dives/async) in Deep dives. This page keeps what is still to come, which turns
out to be one design for two questions. **Concurrency:** a program needs to wait on many things at once without a
thread each. **Crash boundaries:** a crash ends the whole program, and a server can't let one bad request do that.
Tasks are filed in stages ([Plan](#plan)); the crash-boundary parts are proposals.

Two things are fixed already and constrain everything below:

- **Nanyx has no exceptions,** and won't get them. Failures are tag unions ([Error handling](../tour/error-handling)),
  and a crash is not something code catches.
- **Contexts are capabilities.** What a function may do is in its type, and a host decides what a program gets
  ([Hosts](./hosts)).

# What a crash does

`crash`, `orCrash`, a failed `assert`, a [deferred error](../tour/error-handling#deferred-errors) and a non-exhaustive match all write a
message to stderr and end in WebAssembly's `unreachable`, which **traps**. A trap unwinds the instance's whole stack
back to the host, and leaves its linear memory exactly as it was: reference counts half updated, an in-place update
half done, the allocator's lists mid-change. Nothing can safely run in that memory again.

That rules out the obvious designs:

- **Go's `recover`, or `try`/`catch` by another name.** Code would carry on in memory it can't trust. It would also
  be exceptions.
- **`try`/`finally` inside the crashing code.** The `finally` block would run in the same untrusted memory. What
  `finally` is usually for, releasing a file or a socket, Nanyx already puts with the host, which owns every
  resource behind `FileSystem`, `Http` and the rest, and can release them when an instance dies. And observing that a
  crash happened is better done from outside, as below.

So the unit a crash can be contained in is **an instance**: its own linear memory, which a trap leaves the others'
untouched. The [HTTP host](../advanced/hosts#the-http-host) runs each request in one, and the proposed
[test runner](./testing) runs each test in one.

# Recommendation in brief

1. **Crash reports and a host crash hook.** Small, useful now, needed by everything else.
2. **Async in direct style:** the `Async` context, which is built, and structured concurrency, with no
   `async`/`await` keywords and no coloured functions.
3. **Two levels of concurrency.** *Tasks* share an instance: cheap, and they crash together. *Processes* are
   instances: isolated, and a crash in one is a value to the others.
4. **`isolate` first, processes and supervision after,** as a package on top.

# 1. Crash reports and a crash hook

**The report is built** ([#2933](https://github.com/nanyx-lang/nanyx/issues/2933)): a crash hands the runtime its
message, the place in the program's code it crashed at and the stack, as data (`RunError.Crashed`, see
[Crash stacks](../deep-dives/crash-stacks#the-runtime)). The place is a file, a line and a column
([#2950](https://github.com/nanyx-lang/nanyx/issues/2950)). What is still open is the hook: a host
deciding what a crash does, rather than the runtime. The design:

```nanyx fragment
type Crash = (
  message: string
  span: #some(SourceSpan) | #none    -- where crash was called, or the deferred error
  stack: list(string)                 -- function names, innermost first
)
```

The host decides what happens: the console host prints it and exits with 1, as today; the HTTP host answers that
request with a 500 and keeps serving; the test runner marks the test failed and runs the next. The stack comes from
the module's table of places, which debug and release builds both keep, so each frame has its file and line (see
[Crash stacks](../deep-dives/crash-stacks)); today the runtime prints it under the message.

This needs no language feature and makes every later step better, so it comes first.

# 2. Async in direct style

Direct style is decided and built: a function that can wait requires `Async`, there is no `await`
keyword, and a host that can suspend provides `Async` ([Async](../deep-dives/async)). What follows is why it
is safe, what is built of tasks, and what is still open.

The test host, when there is one ([Testing](./testing)), provides `Async` too, and can provide a deterministic one,
with simulated time and a fixed task order, so a test of `timeout` or `race` runs instantly and the same way every
time.

## Rules that make hidden suspension safe

Explicit `await` exists to mark where other code can run in between, so that a reader knows where shared state may
change. Direct style hides those points, which is safe in Nanyx under two rules:

- **Values are immutable,** so another task can never change a value you hold, only make its own.
- **A task can't capture scoped mutable state.** Starting a task refuses a function that captures anything with a
  scoped context (an `MRef`, an `MList`), as `isolate` does. Within one task, code runs in order and nothing else
  can touch its cells; between tasks, only immutable values are shared.

And one rule that keeps the feature small:

- **Only the host's `Async` can suspend.** A context whose member suspends the caller and resumes it later is an
  algebraic effect. Letting any user context do that (generators, backtracking, a user-written scheduler) would make
  contexts a full effect system, as in Koka and OCaml 5. That can be added later without breaking anything; it isn't
  needed for async.

## Structured concurrency

`start!`, `await`, each def as the scope of its tasks, `concurrent` for a smaller one, `race`, cancellation and the
linter's `unawaitedTask` are built: how to use them is on [Async](../advanced/async), and how they are built on
[Async](../deep-dives/async#tasks-and-scopes) in Deep dives.

Why structured, not free `spawn`: a task can't leak, a def's type says everything it needs, and the scope is where a
task's crash and cancellation go. A free-running task is a process (below), which is explicit about it.

## Cancellation

Cancellation arrives as a value at the next point where a task waits, as decided with the maintainer on 2026-11-17
([#1890](https://github.com/nanyx-lang/nanyx/issues/1890)), and is built: `#cancelled` is a case of `HttpError` and
`FsError`, cancelling a task cancels every task it started, `await` never reports it, and `race` cancels the losers
([Cancelling](../advanced/async#cancelling)). Still open:

- **`timeout`,** which gives `#some(a) | #timedOut`, cancelling the task when time runs out: `race` against a sleep does
  it today, so it is a convenience.
- **What else cancels a task.** Only `race` does today; a crash boundary or a process that ends may too (below).

## How it runs

The language doesn't change between these; the hosts and the backend do. Tasks run inside one instance, compiled to
state machines by the backend, with a scheduler that runs an instance's tasks on one stack and waits on the host when
they all wait (decided 2026-11-20 and built for [#1890](https://github.com/nanyx-lang/nanyx/issues/1890); see
[How tasks are compiled](../deep-dives/async#how-tasks-are-compiled)). A request or a sleep lets the instance's other
tasks run while it waits, on the console, HTTP and playground hosts; a file's function blocks its instance's thread
([Async](../deep-dives/async#how-its-built)).

- **Instances on fibers, maybe** ([#1901](https://github.com/nanyx-lang/nanyx/issues/1901)). Running an instance on
  Wasmtime's own fiber stack, so that a wait suspends it and frees the thread, would scale further, but the .NET
  embedding has no async API: it would mean calling Wasmtime's C API directly and running .NET host functions on
  Wasmtime's stacks, which .NET doesn't support. A possible later optimisation. Tasks can also be built on instances
  by running each as a process (below), at the cost of copying.
- **Stack switching, later** ([#2164](https://github.com/nanyx-lang/nanyx/issues/2164)). WebAssembly's
  stack-switching proposal gives a module many stacks and lets it switch between them, so a task is a stack and
  starting one costs about as much as a closure. The proposal is still experimental: Wasmtime's implementation covers
  only some platforms and panics on Windows, and no browser ships it (Chrome's JSPI is narrower). Once it is standard
  and widely supported, it becomes a second way to compile tasks, behind the same language.

A host that can't suspend at all (a plain WASI command with no async support) doesn't provide `Async`, and a program
that needs it doesn't type-check there. That's the capability model doing its job.

## Streams: async sequences

A [lazy sequence](../deep-dives/lazy-sequences) carries the contexts its steps need, `[k] seq(a)`, so a sequence whose steps wait
is just a `seq` with `Async` among them. That's what Kotlin calls a *flow*: a stream of values that are produced
one at a time, possibly waiting between them, and only when someone reads them. `file.lines` has this shape,
with `FileSystem`.

Kotlin's first flow example:

```kotlin
flow { for (i in 1..3) { delay(100); emit(i) } }
    .map { it * 2 }
    .collect { println(it) }
```

In Nanyx:

```nanyx fragment
def ticks: [Async] seq(int) = seq.unfold(1) { i ->
  if i > 3 then #done
  else
    sleep(100ms)
    #some(i, i + 1)
}

for ticks \seq.map { * 2 } | n -> println("{n}")
```

A server reading events as they arrive looks the same:

```nanyx fragment
def events: [Async, Http] string -> [Async, Http] seq(Event) = { url ->
  http.stream(url) \seq.map(parseEvent)
}

for events(feedUrl) \seq.filter { e -> e.level == #error } \seq.take(10) | e -> alert(e)
```

| Kotlin | Nanyx |
|---|---|
| `flow { emit(x) }` | `seq { yield x }` ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)), or `seq.unfold` |
| Cold: nothing runs until it's collected | The same: a `seq` runs only as it's pulled |
| `map`, `filter`, `take`, `fold` | `seq.map`, `seq.filter`, `seq.take`, `seq.fold`, with no async versions, since they're generic over contexts |
| `collect` | a `for` loop, or `seq.fold` |
| A `suspend` lambda in `map` | an `[Async]` lambda, which makes the result an `[Async] seq` |
| Cancelling the collector's scope stops the flow | cancelling the task that's pulling it |

Kotlin needs `Flow` beside `Sequence` because `Sequence`'s operators can't suspend. Nanyx's `seq` operators take
`[c]` functions, so one type is both.

**Where it differs:**

- **Pull, not push.** A Kotlin flow's producer calls the collector; a `seq`'s consumer pulls each element. For a
  pipeline that runs in one task the two behave the same. The difference shows only when the producer and consumer
  run at the same time, which is the next point.
- **Concurrent operators are library code over channels.** `buffer`, `conflate`, `flatMapMerge` and `flowOn` run the
  producer in a coroutine of its own. In Nanyx each starts a task that pulls the source and
  sends into a bounded channel, and gives back a `seq` that reads the channel: `events(url) \seq.buffer(16)`. They
  need tasks and channels, so they come after the plain operators.
- **Hot streams are processes.** Kotlin's `SharedFlow` and `StateFlow` run whether or not anyone is collecting and
  have many subscribers. In Nanyx that's a [process](#processes-mailboxes-and-supervision) that subscribers register
  with, not a `seq`.
- **Cancellation ends the sequence.** A waiting step in a cancelled task gets `#cancelled`, but a `seq`'s elements
  have nowhere to put it. Proposed: a pull in a cancelled task gives `#done`, so the sequence ends and a `for` loop over
  it finishes normally. Unlike a failed read in `file.lines`, nothing is lost by ending quietly: the scope that was
  cancelled already knows.

# 3. Tasks and processes

| | Task | Process |
|---|---|---|
| Lives in | its parent's instance, on its own stack | its own instance |
| Shares | memory with its siblings | nothing; values are copied in and out |
| Costs | about a closure | an instance (microseconds, with Wasmtime's pooling allocator) and the copies |
| On a crash | the whole instance goes down, its siblings too | it ends; the code that started it gets `#crashed(Crash)` |
| Use for | waiting on many things at once | isolating something that may crash, and long-lived workers |

Tasks crashing together is what a structured scope does anyway: Trio cancels a nursery's other tasks when one fails.
Isolation is opt-in and visible in the code, because it costs copying.

# 4. `isolate`, then processes

## `isolate`: a crash boundary as a value

The one primitive the rest is built on:

```nanyx fragment
isolate: [Isolate] (() -> a) -> #some(a) | #crashed(Crash)

match isolate { parse(input) \evaluate }
  | #some(result) -> println("{result}")
  | #crashed(c) -> println("That crashed: {c.message}")
```

The function runs in a fresh instance of the same module. What it captures is deep-copied in, and its result is
deep-copied out. Nothing is caught inside the failing code: the boundary turns "that instance died" into a tag,
which is how every other failure looks. `Isolate` is a context the host provides, like `Console`.

- **What can cross is already decided by contexts.** A value whose type carries a scoped context (`[Memory]`, so an
  `MRef`, an `MList`) can't leave its scope, so it can't be captured by the function or returned. Shared mutable
  state between processes can't be written.
- **The copy is generated code,** as `==` and `dbg` are: the module knows its own layouts. A protected type's
  invariants survive, since its value is copied as it is, never rebuilt. A closure can cross: the code is the same
  module's, and its captures are copied.
- **Who's outside a boundary** gets the resources: when an instance dies, the host closes the files and connections it
  held.

The test runner and the HTTP host don't need it: the runtime runs each test, and each request, in a fresh instance of
its own, and needs only crash reports from it ([Testing](./testing#decided)). Uses before there are any processes:

- A **playground or REPL**: a crashing snippet doesn't lose the session.
- **Plugins**: running code the program doesn't trust, with only the contexts it's given.

## Processes, mailboxes and supervision

A process is a long-lived `isolate` with a mailbox, in a package (`nanyx.process`, a working name). Messages are
**typed**, as Gleam's OTP does it, not Erlang's untyped mailboxes, so a tag union is the protocol:

```nanyx fragment
type CounterMessage = #increment | #get(reply: Subject(int))

def counter: [Process(CounterMessage)] int -> () = { count ->
  match receive()
    | #increment -> counter(count + 1)
    | #get(reply) ->
        reply \send(count)
        counter(count)
}

def main = {
  def c = process.start { counter(0) }
  c \send(#increment)
  println("{c \call { reply -> #get(reply) }}")      -- send and wait for the answer
}
```

A process's loop is a tail-recursive function, as in Erlang; its state is its argument.

Supervision is ordinary library code: a supervisor is a process that starts children, gets `#crashed(Crash)` when one
ends, and restarts it by a policy (one for one, one for all), as OTP does:

```nanyx fragment
def main = supervise(strategy = #oneForOne, [
  child("cache") { cache.run(capacity = 1000) },
  child("worker") { worker.run() },
])
```

Processes can be scoped too: `supervise` returns only when its children have stopped, so a crash's reach is visible
in the code.

# Open questions

- **Fresh or reused instances.** `isolate` in a fresh instance gives clean memory; reusing a warm one is faster but
  needs a reset, and anything left behind leaks between calls.
- **Is the top level supervised?** Erlang-style, `main` could run under a supervisor by default, so a crash in any
  process is contained unless the program says otherwise. Or isolation stays opt-in.
- **Typed channels first, or `isolate` alone?** `isolate` covers tests, the HTTP host and plugins; processes can wait
  for a real use.
- **Wasm exceptions for user crashes.** WebAssembly 3.0 has `throw` and `try_table`. A `crash` that throws could make
  a boundary cheaper and let the compiler drop reference counts as it unwinds. Real traps (stack overflow, out of
  memory) still need the instance boundary, so this would be an optimisation, not a replacement.
- **Effect handlers in general.** Only the host's `Async` suspends, for now. [Effect handlers](./effect-handlers)
  proposes making it the first case of a general feature, with `yield` the second, and records a spike of a generator
  on async's state machines.
- **A cancelled stream.** Does a pull in a cancelled task give `#done`, as [proposed](#streams-async-sequences)?
- **Channels.** Bounded by default, and what a send to a full one does: wait (back-pressure), drop, or keep only the
  latest (`conflate`).

# Plan

The rest of async is filed in stages. The crash-boundary steps are proposals, not filed.

| Step | Issue | Stage |
|---|---|---|
| Async 3: channels, and `seq.buffer`, `conflate` and `merge` | [#1891](https://github.com/nanyx-lang/nanyx/issues/1891) | Planning: full channels, cancelled streams |
| Tasks on stack switching, once it's standard and widely supported | [#2164](https://github.com/nanyx-lang/nanyx/issues/2164) | Planning |
| Crash reports: `Crash` with message, span and stack, used by the console, HTTP and test hosts | [#2933](https://github.com/nanyx-lang/nanyx/issues/2933) | Ready (testing's stage 1) |
| `isolate` and the deep copy, once a use needs it that a host's instance per call can't cover | | proposed |
| `nanyx.process`: typed mailboxes and supervisors, and hot streams on them | | proposed |
