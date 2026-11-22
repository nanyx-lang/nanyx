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

## `Async`

```nanyx fragment
context Async = protected ()
```

The capability to wait. It has no members: the functions that wait, such as `http.get` and
`file.read`, require it, so their type says that they can. Only a host provides it: its prelude
exports `Async` and its `run` requires it, so the program's top level has it, and the host's `run`
passes it on to the entry point. It is protected, so no `use` outside this module can make one,
and a host that can't suspend doesn't provide it, so a program that needs it is an error there.

<!-- End of the generated reference. -->