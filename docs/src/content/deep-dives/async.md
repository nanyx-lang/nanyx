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
