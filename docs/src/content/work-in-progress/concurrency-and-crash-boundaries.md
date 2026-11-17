---
title: "Concurrency and crash boundaries"
description: "Async, tasks, isolated processes, and keeping a crash from taking the whole program down"
order: 6
---

Two questions that turn out to be one design. **Async:** an HTTP client, and soon a server, needs to wait on many
things at once without a thread each. **Crash boundaries:** a crash ends the whole program today, and a server
can't let one bad request do that. This page collects the ideas and recommends a direction. Nothing here is decided
or built.

Two things are fixed already and constrain everything below:

- **Nanyx has no exceptions,** and won't get them. Failures are tag unions ([Error handling](../tour/error-handling)),
  and a crash is not something code catches.
- **Contexts are capabilities.** What a function may do is in its type, and a host decides what a program gets
  ([Hosts](./hosts)).

# What a crash is today

`crash`, `orCrash`, a failed `assert`, a [deferred error](./deferred-errors) and a non-exhaustive match all write a
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
untouched. The [test runner](./testing) already relies on this, a fresh instance per test, and the
[HTTP host](./hosts#serving-http) runs each request in one.

# Recommendation in brief

1. **Crash reports and a host crash hook.** Small, useful now, needed by everything else.
2. **Async in direct style:** an `Async` context and structured concurrency, with no `async`/`await` keywords and no
   coloured functions.
3. **Two levels of concurrency.** *Tasks* share an instance: cheap, and they crash together. *Processes* are
   instances: isolated, and a crash in one is a value to the others.
4. **`isolate` first, processes and supervision after,** as a package on top.

# 1. Crash reports and a crash hook

A crash hands the host a structured report rather than only text on stderr:

```nanyx fragment
type Crash = (
  message: string
  span: #some(SourceSpan) | #none    -- where crash was called, or the deferred error
  stack: list(string)                 -- function names, innermost first
)
```

The host decides what happens: the console host prints it and exits with 1, as today; the HTTP host answers that
request with a 500 and keeps serving; the test runner marks the test failed and runs the next. The stack comes from
the module's name section, so a release build that strips it reports the message and span only.

This needs no language feature and makes every later step better, so it comes first.

# 2. Async in direct style

## The choice

There are three ways to give Nanyx async.

**A. A workflow,** as the [Workflows](../advanced/workflows#async-and-custom-keywords) page sketches today:

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

There's no `await`: a call that waits suspends the calling task until it can carry on, as a blocking call reads.
The colour is still in the type, as a context, but contexts are already polymorphic: `list.map` takes a
`[c] a -> b`, so it maps an `[Async]` function with no second version. Inference adds `Async` to a function that
calls one, as it adds `Console`. This is the model of OCaml 5's effects, Go, Java's virtual threads, and Flix and
Unison's effect systems, and Nanyx's contexts are already most of the way there.

**C. Every call can block, and nothing says so.** Go's model without the type. Simplest to write, but it loses what
contexts are for: a function's type no longer says whether it can wait, and a host that can't suspend has no way to
refuse a program that would.

**Recommendation: B.** It keeps capabilities in types, avoids colouring through context polymorphism, and the
workflow form stays available for the cases a workflow is good at (generators, parsers), not for async.

## Where `Async` comes from

Nothing starts it: like `Console`, the host provides it. A host that can suspend (the console host on Wasmtime, the
HTTP host) runs the entry with `Async` in scope, and inference carries it down to every function that calls one that
waits:

```nanyx fragment
::host nanyx.console

def fetchName: [Async, Http] string -> string = { url ->
  def response = http.get(url) \orCrash("no name")
  response.text
}

println("Hello, {fetchName("https://example.com/name")}")    -- the top level already has Async and Http
```

- **No `async { }` block.** "May wait" is the `Async` context, which code has or doesn't. "Runs at the same time" is
  [`concurrently`](#structured-concurrency). Those are the two things an `async` block mixes up in C# or JavaScript.
- **Code without `Async` can't get it.** A library that doesn't ask for it provably never waits, as one that doesn't
  ask for `Console` never prints.
- **No `runBlocking`.** Inside a host that can suspend there's nothing to bridge, and in one that can't, a blocking
  runner would need an event loop the host doesn't have. The missing-context error is the honest answer.
- **The test host provides it too,** and can provide a deterministic one, with simulated time and a fixed task order,
  so a test of `timeout` or `race` runs instantly and the same way every time.

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

Waiting on one thing is a plain call. Doing several at once is a block whose tasks can't outlive it, as Trio's
nurseries and Kotlin's coroutine scopes do:

```nanyx fragment
def both: [Async, Http] () -> (Response, Response) = {
  concurrently { scope ->
    def a = scope \start { http.get(urlA) \orCrash("a failed") }
    def b = scope \start { http.get(urlB) \orCrash("b failed") }
    (a \join, b \join)
  }
}
```

`concurrently` returns only when every task started in it has finished. Most code wants the common shapes, which are
ordinary library functions on top:

```nanyx fragment
def pages: [Async, Http] list(string) -> list(#some(Response) | #error(HttpError)) = { urls ->
  urls \async.all { url -> http.get(url) }               -- every one, at once, in order
}

def fastest = async.race([{ http.get(mirrorA) }, { http.get(mirrorB) }])   -- first to finish; the rest cancelled

def withLimit = async.timeout(seconds = 5) { http.get(url) }               -- #some(r) | #timedOut
```

Why structured, not free `spawn`: a task can't leak, a block's type says everything it needs, and the scope is where
a task's crash and cancellation go. A free-running task is a process (below), which is explicit about it.

## Cancellation without exceptions

A cancelled task has to stop, and there's nothing to throw. **Cancellation arrives as a value at the next point where
the task waits:** every call that waits can give back `#cancelled`, and keeps giving it back for the rest of the
task, so the code returns through its ordinary error handling:

```nanyx fragment
match http.get(url)
  | #some(response) -> response
  | #error(#cancelled) -> ...      -- only after the task was cancelled; usually just passed on with try
  | #error(e) -> ...
```

The task unwinds by returning, so reference counts are dropped as usual and nothing leaks. A loop that never waits
can't be cancelled until it does, as in Go. Whether `#cancelled` is a case of every error type that can wait, or the
waiting calls return `#some(a) | #cancelled | e`, is an [open question](#open-questions).

## How it runs

The language doesn't change between these; the hosts and the backend do.

- **First: a wait suspends the whole instance.** Wasmtime can run an instance on its own stack and suspend it inside
  a host call, so `http.get` blocks only that instance, and the host runs many instances at once. That's the HTTP
  host's one instance per request, available now. `concurrently` can be built on it by running each task as a
  process (below), at the cost of copying.
- **Then: tasks inside one instance, by stack switching.** WebAssembly's stack-switching proposal gives a module
  many stacks and lets it switch between them; it's built for effect handlers, which is what `Async` is. Wasmtime
  implements it; browsers suspend a Wasm stack on a JavaScript promise with JSPI. With it, a task is a stack, and
  starting one costs about as much as a closure.
- **A fallback** for a platform with neither: Binaryen's Asyncify transform, which makes a module able to unwind
  and rewind its own stack, at a cost in code size and speed.

A host that can't suspend at all (a plain WASI command with no async support) doesn't provide `Async`, and a program
that needs it doesn't type-check there. That's the capability model doing its job.

## Streams: async sequences

A [lazy sequence](./lazy-sequences) carries the contexts its steps need, `[k] seq(a)`, so a sequence whose steps wait
is just a `seq` with `Async` among them. That's what Kotlin calls a *flow*: a stream of values that are produced
one at a time, possibly waiting between them, and only when someone reads them. `file.lines` is already this shape,
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
    clock.sleep(ms = 100)
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
| `flow { emit(x) }` | `seq.unfold` today; a `seq { yield x }` builder once [#1699](https://github.com/nanyx-lang/nanyx/issues/1699) is decided |
| Cold: nothing runs until it's collected | The same: a `seq` runs only as it's pulled |
| `map`, `filter`, `take`, `fold` | `seq.map`, `seq.filter`, `seq.take`, `seq.fold`, with no async versions, since they're generic over contexts |
| `collect` | a `for` loop, or `seq.fold` |
| A `suspend` lambda in `map` | an `[Async]` lambda, which makes the result an `[Async] seq` |
| Cancelling the collector's scope stops the flow | cancelling the `concurrently` scope that's pulling it |

Kotlin needs `Flow` beside `Sequence` because `Sequence`'s operators can't suspend. Nanyx's `seq` operators take
`[c]` functions, so one type is both.

**Where it differs:**

- **Pull, not push.** A Kotlin flow's producer calls the collector; a `seq`'s consumer pulls each element. For a
  pipeline that runs in one task the two behave the same. The difference shows only when the producer and consumer
  run at the same time, which is the next point.
- **Concurrent operators are library code over channels.** `buffer`, `conflate`, `flatMapMerge` and `flowOn` run the
  producer in a coroutine of its own. In Nanyx each starts a task in a `concurrently` scope that pulls the source and
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

Uses before there are any processes:

- The **test runner**: a test's crash is `#crashed`, which is what the runner does by hand today.
- The **HTTP host**: "each request in a fresh instance" is `isolate` per request, built from the same primitive.
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

# What this changes on other pages

- [Files, HTTP and JSON](./files-http-json) says every call blocks. With this design, the calls that wait require
  `Async`, and block only their task. `nanyx.fs` and `nanyx.http.core` can ship blocking first, and gain `Async` in
  their types when it exists: closed alpha, so no transition.
- [Hosts](./hosts) leaves async for later and asks whether a host can change how the entry runs. Under this design
  the host provides `Async` or doesn't, and the entry's type says whether it needs it.
- [Workflows](../advanced/workflows#async-and-custom-keywords) uses async as its example of a custom keyword. If B is
  chosen, that example moves to something workflows are the right tool for.

# Open questions

- **A or B.** Direct style with an `Async` context, or workflows. Everything else here assumes B.
- **How cancellation is typed.** A `#cancelled` case in every waiting call's error type, or a wrapper, or a crash
  that only a scope sees?
- **Fresh or reused instances.** `isolate` in a fresh instance gives clean memory; reusing a warm one is faster but
  needs a reset, and anything left behind leaks between calls.
- **Is the top level supervised?** Erlang-style, `main` could run under a supervisor by default, so a crash in any
  process is contained unless the program says otherwise. Or isolation stays opt-in.
- **Typed channels first, or `isolate` alone?** `isolate` covers tests, the HTTP host and plugins; processes can wait
  for a real use.
- **Wasm exceptions for user crashes.** WebAssembly 3.0 has `throw` and `try_table`. A `crash` that throws could make
  a boundary cheaper and let the compiler drop reference counts as it unwinds. Real traps (stack overflow, out of
  memory) still need the instance boundary, so this would be an optimisation, not a replacement.
- **Is hidden suspension acceptable?** The [rules above](#rules-that-make-hidden-suspension-safe) mean nothing a task
  can see changes across a wait, which is what explicit `await` protects. Some people still want to see where code
  can be interrupted.
- **Effect handlers in general.** Only the host's `Async` suspends, for now. Should `Async` be designed as the first
  case of a general feature that user contexts get later?
- **A cancelled stream.** Does a pull in a cancelled task give `#done`, as [proposed](#streams-async-sequences)?
- **Channels.** Bounded by default, and what a send to a full one does: wait (back-pressure), drop, or keep only the
  latest (`conflate`).

# Plan

Not yet filed. In order:

1. Crash reports: `Crash` with message, span and stack, and the console, HTTP and test hosts using it.
2. `isolate`, the deep copy, and the test runner and HTTP host built on it.
3. Decide A or B. If B: the `Async` context, `concurrently`, `async.all`, `race` and `timeout`, on instance
   suspension first.
4. Tasks in one instance, by stack switching, with Asyncify as the fallback.
5. Channels, and the concurrent stream operators on them: `seq.buffer`, `conflate`, `merge`.
6. `nanyx.process`: typed mailboxes and supervisors, and hot streams on them.
