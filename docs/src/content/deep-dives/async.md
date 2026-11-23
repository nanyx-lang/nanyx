---
title: "Async"
description: "How a function that can wait is typed and run, and why Nanyx waits in direct style"
order: 20
---

A function that can wait requires the [`Async`](../stdlib/async) context, which the console and HTTP hosts provide
([Hosts](../advanced/hosts#the-console-host)). This page says how it is built and why it has this shape. Tasks,
cancellation, async streams and crash boundaries are not built yet; they are on
[Concurrency and crash boundaries](../work-in-progress/concurrency-and-crash-boundaries) in Work in progress.

# How it's built

- **A context with no members.** `nanyx/async` declares `Async` as `protected ()`, so no `use` outside that module can
  make one, and a function that requires it is passed nothing. A host that can suspend provides it: its prelude
  re-exports it and its `run` requires it and passes it to the entry. Inference adds it to every function that calls
  one that waits, as it adds `Console`. How the compiler makes a memberless context ambient is on
  [Hosts](./hosts#how-its-built).
- **A host that can't suspend doesn't provide it.** The playground host doesn't, so a program that needs `Async` is an
  error naming the host (`NYX0205`), as for any context a host doesn't provide.
- **A wait holds only its instance.** Each instance runs on a thread of its own, and a call that waits is a host call
  that blocks that thread. The console host has one instance; the HTTP host runs each request's instance on a thread
  of its own (`WasmServer.serve`), so a handler that waits on an outbound request holds only its own, and the host
  answers other requests meanwhile.
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
([#2164](https://github.com/nanyx-lang/nanyx/issues/2164)). Nothing stops yet: a wait still blocks its instance's thread
(above), so today every call that could stop finishes, and the state machines are the path every `Async` program takes
without ever stopping.

- **Split points.** A function is a state machine when it can stop: when it calls a function value whose type waits,
  or a function that is a state machine itself (`CoreIrSplitPoints.stateMachines`, over the module once it is counted).
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
  computed, are kept in locals instead (`SplitPlan.StoredTemps`).
- **Running one to the end.** Code that calls a state machine without being one, the start function, the getter of a
  cached value and a component's export, resumes it while it stops (`TaskLowering.blockOn`). With no tasks yet, that
  is the whole scheduler.
- **Testing it.** Since nothing stops yet, `WasmOptions.YieldAtEveryWait` (`nanyx run --yield-at-every-wait`, for
  working on the compiler) makes every call of a function value that can wait stop once before it is made, through
  `__nanyx_yield`, which has a frame of its own. The tests run programs that wait both ways, with memory checks, and
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
