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
   isolated, here or on another machine, and a crash in one is a value to the others. They come in three shapes: run
   and stop, which is a task in another instance; stages, on channels; and servers and actors, which are called.
4. **Design from the remote case down.** What can be sent, how a process fails and how it is addressed are decided for
   processes on other machines; local processes, and `isolate`, are the smallest cases.

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
  have many subscribers. In Nanyx that's a [process](#3-tasks-and-processes) that subscribers register
  with, not a `seq`.
- **Cancellation ends the sequence.** A waiting step in a cancelled task gets `#cancelled`, but a `seq`'s elements
  have nowhere to put it. Proposed: a pull in a cancelled task gives `#done`, so the sequence ends and a `for` loop over
  it finishes normally. Unlike a failed read in `file.lines`, nothing is lost by ending quietly: the scope that was
  cancelled already knows.

# 3. Tasks and processes

Nanyx has two levels of concurrency. *Tasks* share an instance: they're cheap, and they crash together. *Processes* are
isolated: each has memory of its own, possibly on another machine, and a crash in one is a value to the others.

| | Task | Process |
|---|---|---|
| Lives in | its parent's instance, on its own stack | an instance of its own, here or on another machine |
| Shares | memory with its siblings | nothing; what crosses is sent (below) |
| Costs | about a closure | an instance (microseconds, with Wasmtime's pooling allocator) and the sends |
| On a crash | the whole instance goes down, its siblings too | it ends, and its outcome says so |
| Use for | waiting on many things at once | isolating something that may crash, workers, servers and actors |

Tasks crashing together is what a structured scope does anyway: Trio cancels a nursery's other tasks when one fails.
Isolation is opt-in and visible in the code, because it costs an instance and copying.

The rest of this section is the design as discussed with the maintainer on 2026-11-27: proposals, not filed. Erlang-style
processes and Orleans-style actors, which may run on other machines, are where it is meant to go, so it is designed from
that case down, and the local cases are the smallest ones.

## Three shapes

A process takes input once, when it starts, or over time, and gives output once, when it ends, or over time:

| | Output once | Output over time |
|---|---|---|
| **Input once** | run and stop | a producer: a `seq` filled from another instance |
| **Input over time** | a consumer, or a fold | a stage, a server or an actor |

That's a Unix process: arguments and an exit code, and stdin and stdout for anything long-lived. Most systems draw the
same line in their libraries, over one primitive: Elixir's `Task` and `GenServer`, Kotlin's `async` and `actor`, Swift's
`Task` and `actor`. Erlang has only the process; a task is a process that sends one message and stops.

Nanyx gives users three shapes, each library code over one primitive, the process, which the host provides:

- **Run and stop** is a task that runs in another instance. It has what a task has: `start!`, `await`, `race`,
  cancellation and a scope. What differs is that what it captures must be sendable, and that its outcome can be a crash
  or an unreachable machine as well as its value.
- **A stage** is a step of a pipeline, between one input and one output: a parser between a socket and a handler, or
  `seq.buffer` and `seq.merge` ([#1891](https://github.com/nanyx-lang/nanyx/issues/1891)) running elsewhere. It talks
  over **channels**, one in and one out.
- **A server or an actor** has many clients, each of which wants its own reply, which one shared output channel can't
  route. It is **called**: a request goes in, and a typed reply comes back to the caller.

## Every process ends with an outcome

A long-lived process ends too: it stops, crashes, or its machine goes away, and a supervisor needs to know which. So
every process has an outcome when it ends, as every Unix process has an exit code, and channels are an addition:

```nanyx fragment
type Exit(a) = #some(a) | #crashed(Crash) | #unreachable
```

A run-and-stop process's `await` gives its `Exit`. A stage or a server has one as well, which its owner sees.

## Who owns a process's lifetime

This is a second axis, apart from the shape:

- **The caller's scope,** as for tasks today: the scope ends only when its processes have, so a crash's reach is visible
  in the code. Run-and-stop processes and stages fit here.
- **A supervisor,** which starts children, gets each one's outcome when it ends, and restarts it by a policy (one for one,
  one for all), as OTP does. A supervisor is a process, and library code.
- **The runtime,** for actors as Orleans has them: an actor exists by its key, is never started or stopped by the
  program, and the runtime activates it, where it likes, when it is called.

```nanyx fragment
def main = supervise(strategy = #oneForOne, [
  child("cache") { cache.run(capacity = 1000) },
  child("worker") { worker.run() },
])
```

`supervise` returns only when its children have stopped, so a supervisor's children are scoped too.

## Calling a server or an actor

There are two ways to write a call.

**A typed message with a reply address,** as Gleam's OTP does. The protocol is a tag union, which is easy to send and to
version, but every call builds a message, and a reply's type is tied to its request only by convention:

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
  println("{c \call { reply -> #get(reply) }}")
}
```

**An interface,** a record of functions that the runtime gives a proxy for, as Orleans grain interfaces and Swift's
distributed actors are. Calls read as calls, each reply's type is checked, and a call's failures are in each member's
type:

```nanyx fragment
type CallError = #crashed(Crash) | #unreachable | #timedOut

type Counter = (increment: [Async] () -> #ok | CallError, get: [Async] () -> #some(int) | CallError)

def c: Counter = actors.get("counter-42")
```

Proposed: users write interfaces, and they are built on messages. An interface compiles to a tag union of requests, one
case per member, each with a reply address of its member's type, and that union is what is sent. The client gets typed
calls, the runtime one simple message format to send over the wire, and a router or a supervisor can still handle the
messages themselves. A server's loop is a tail-recursive function, as in Erlang, its state its argument.

# 4. What crosses a boundary

What tasks and every shape of process need from the runtime is the same: memory of their own, values sent between
memories, and an outcome as a value when one ends. These are decided for a process on another machine, the hardest case,
so that a local process and `isolate` are smaller cases of the same rules, never ones that a remote process must narrow
later.

## Sendable types

A value crosses to another instance by being copied, and to another machine by being written as bytes, for a build of
the program that may be a different one. So which types can be sent is a property the compiler works out from a type,
as it works out what can leave a scope:

- **Data** (numbers, strings, records, tag unions, lists of them) can be sent anywhere.
- **Values with a scoped context** (`[Memory]`, `[TaskScope]`: an `MRef`, an `MList`, a `Task`) can't be sent at all,
  as they can't leave their scope. Shared mutable state between processes can't be written.
- **Closures** are open (below). In one build, a closure's code is the same on both sides and only its captures need
  copying. Between builds it isn't: Erlang fails with `badfun` when a fun is sent to a node with another version of its
  module, Orleans sends no closures, only calls on interfaces, and Swift's distributed actors require every argument to
  meet a serialization requirement (`Codable`).

The copy and the bytes are generated code, as `==` and `dbg` are: the module knows its own layouts. A protected type's
invariants survive, since its value is copied as it is, never rebuilt.

## Failing remotely

A call to another machine can fail when nothing crashed: the machine is unreachable, the network is split, or no reply
comes in time. "A Note on Distributed Computing" (Waldo et al., 1994) argues that hiding that from callers breaks
programs; Erlang hides it, and pays with a timeout on every `gen_server:call`, and every Orleans grain call can time out.
So a process's outcome and a call's failures include `#unreachable`, and a call's `#timedOut`, from the start, even
while every process is local.

## Addressing and versions

A process has an identity that can be sent, so that it can be given to other processes and mean the same on another
machine. An actor is addressed by a key, not by a reference. And a cluster runs two builds at once during a deploy, so a
protocol (a message union, or an interface's members) is a public API, and changing it is the same question the
release's public API check asks of a package ([Releases](../deep-dives/releases#the-shipped-packages)).

## Hosts own the mechanism, types own the meaning

A context's type is its contract, and a host decides how to keep it, not what it means: the host decides where
`println` goes, not what it does. So a host that provides processes decides whether each is a fresh instance or a warm
one, a thread, a fiber or another machine, and its limits on fuel, memory and time. It doesn't decide that a process is
something else.

Services and actors aren't other meanings of one context:

- **A service, or an Azure Functions-style function, is an entry point,** which hosts already own: what a program is,
  who starts it and how long it lives is the shape of `@entry` and the host's `run`, as the
  [HTTP host](../advanced/hosts#the-http-host) makes a program a request handler.
- **Actors are a context of their own,** with a contract that says what an actor is. A library can provide it on
  processes, for any host that has them, and an Orleans-style host can provide it natively, with real placement,
  activation and persistence. A program written against it runs on either.

## `isolate`

`isolate` is the smallest case: run a function in a fresh instance of the same module, wait, and get its value or its
crash.

```nanyx fragment
isolate: [Isolate] (() -> a) -> #some(a) | #crashed(Crash)

match isolate { parse(input) \evaluate }
  | #some(result) -> println("{result}")
  | #crashed(c) -> println("That crashed: {c.message}")
```

Nothing is caught inside the failing code: the boundary turns "that instance died" into a tag, which is how every other
failure looks. When an instance dies, the host closes the files and connections it held.

It is no longer the primitive the rest is built on. It's a run-and-stop process, local and awaited at once, and can be
library code over processes. The test runner and the HTTP host don't need it: the runtime runs each test, and each
request, in a fresh instance of its own, and needs only crash reports from it ([Testing](./testing#decided)). What it
offers code in the same module is a boundary for what the type checker can't rule out: a crash, and with the host's
limits, a loop that never ends or memory that runs out. A **playground or REPL** is the use: a snippet that crashes or
never ends doesn't lose the session.

**Plugins aren't a use.** `isolate` runs the program's own code, which was type-checked with it, and a function's
contexts already say what it may do: `() -> a` can't print, whatever instance it runs in. Code the program doesn't trust
is another module, loaded while it runs, with its imports linked to the capabilities it is given. That is what the
component model does ([Components](./components)), and it is a different primitive from `isolate`: a closure can't
cross to another module, whose code doesn't have its function.

# Open questions

- **Local and remote: the same, or visibly different?** Erlang makes a remote process look like a local one; Orleans,
  and the Waldo paper, make a remote call visibly one that can fail. With failures in every call's type, as proposed,
  the question is whether one context covers both, or a remote one is a context of its own.
- **Can closures be sent?** Sending only data is simpler and works between machines, but loses a supervisor's "restart
  by calling this function", for which a child would name a registered function, as an OTP child spec names a module, a
  function and its arguments. Or closures are sendable only within one build, which the type would have to say.
- **Is an actor reference a context?** An interface is a record of functions someone else provides, which is close to
  what a context is: a function that requires `[Counter]` could be given a local, a remote or a fake one, as the test
  host loads fakes. It doesn't fit a program that holds many counters, rather than one in scope.
- **Is `Isolate` a context?** It reaches nothing outside the program, but costs an instance, and not every host can
  give it, as not every host can suspend for `Async`. The alternative is a built-in every host supports, with only its
  limits the host's choice.
- **Fresh or reused instances.** A fresh instance gives clean memory; reusing a warm one is faster but needs a reset,
  and anything left behind leaks between calls.
- **Is the top level supervised?** Erlang-style, `main` could run under a supervisor by default, so a crash in any
  process is contained unless the program says otherwise. Or isolation stays opt-in.
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

The rest of async is filed in stages. The process steps are proposals, not filed.

| Step | Issue | Stage |
|---|---|---|
| Async 3: channels, and `seq.buffer`, `conflate` and `merge` | [#1891](https://github.com/nanyx-lang/nanyx/issues/1891) | Planning: full channels, cancelled streams |
| Tasks on stack switching, once it's standard and widely supported | [#2164](https://github.com/nanyx-lang/nanyx/issues/2164) | Planning |
| Crash reports: `Crash` with message, span and stack, used by the console, HTTP and test hosts | [#2933](https://github.com/nanyx-lang/nanyx/issues/2933) | Ready (testing's stage 1) |
| Sendable types, process outcomes and addressing, designed for processes on other machines | | proposed |
| Processes in the host, and run-and-stop processes as tasks in other instances | | proposed |
| Stages on channels, servers and actors on interfaces, and supervisors | | proposed |
| `isolate`, as library code over processes, once a playground or REPL needs it | | proposed |
