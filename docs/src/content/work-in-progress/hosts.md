---
title: "Hosts"
description: "Hosts still to come (in a page, a browser, HTTP, tests), and the design behind them"
order: 3
---

What works today, choosing a host, the console host, writing a host package, foreign functions and host memory, is
described in [Hosts](../advanced/hosts), and how the compiler builds hosts in [Hosts](../compiler-internals/hosts) in the
Compiler internals section. This page keeps the hosts still to come, the design they follow, and the open questions.

A command-line tool, an HTTP service, a browser app and a test run are all the same program model running under
different hosts.

# Motivation

Today the entry function has one special case built in: it [runs in `Console`](../advanced/contexts#console-by-default). That is already an implicit host, with a single hard-coded answer to three questions:

1. What shape must the entry function have?
2. Which contexts are available to it?
3. Who owns the process: the main loop, concurrency, signals, shutdown and the exit code?

Different kinds of program need different answers. An HTTP service wants its entry to be a `Request -> Response` handler and shouldn't have to write its own accept loop. A sandboxed plugin should get *fewer* capabilities than a CLI tool. A test run wants fake or recording implementations of the same contexts the real program uses.

Hosts make these answers explicit and swappable, and let the compiler check them.

# The entry shape is just a type

Each host takes exactly one entry shape. Under the console host the entry is `() -> ()`, and a program's arguments and exit code come from the `Env` context it provides. Under an HTTP host the entry *is* the handler:

```nanyx
module main

@entry
def main: Request -> Response = { req ->
  match req.path
    | "/" -> Response.text(200, "Welcome to Nanyx!")
    | #Path('/hello/{name}')(name) -> Response.text(200, "Hello, {name}!")
    | _ -> Response.text(404, "Not Found")
}
```

The port comes from configuration, and listening, concurrency and graceful shutdown belong to the host. This is how the [HTTP server cookbook](../cookbook/http-server) works today, though the `#Path` route pattern isn't built.

If the entry has the wrong shape, the error names the host:

```
error: the entry `main` doesn't match the host `nanyx.http/host`
  expected: [Log, Clock] Request -> Response
  found:    [Console] () -> ()
```

# Hosts provide contexts

A host provides the contexts the program's entry may require, and only those. This is where hosts and [contexts](../advanced/contexts) fit together:

| Host | Entry shape | Provides |
| --- | --- | --- |
| `ConsoleHost` | `() -> ()` | `Console & Env & FileSystem & Http & Random & Clock` |
| HTTP host | `Request -> Response` | `Log & Clock & Env & Random & Http` (see [Serving HTTP](#serving-http)) |
| Browser host | a record of `init`, `update` and `view` | `Dom & Fetch & Clock & Log` |
| Plugin / sandbox host | host-defined | only what the embedder grants |
| Test host | test functions | fakes or recorders of the above |

Libraries stay host-agnostic because they never assume a host. They declare what they need, such as `[FileSystem]`, and the compiler checks that the chosen host provides it:

```
error: `config.load` requires context `FileSystem`, which `nanyx.http/host` does not provide
```

This gives capability security as a side effect. A host that doesn't provide `FileSystem` guarantees the program can't touch the file system. That includes every library the program uses.

# Hosts on WebAssembly

On the WASM backend, a host maps directly onto a set of imports and exports: the "world" the module is instantiated in. The WASI `wasi:cli` and `wasi:http` worlds are natural targets for `ConsoleHost` and an HTTP host.

# Running in a browser

A browser can be the embedder for two different hosts. Neither works today.

## Console programs in a page

A console program can run in a page unchanged, with the page standing in for the terminal. This is what a [playground](./playground) on the docs site needs. It runs under a host of its own, `nanyx.playground`, a stand-in for the console host whose imports a page implements, rather than under the console host with the page faking WASI ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)).

- Done: **the toolchain writes `.wasm`** ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)). `nanyx wasm` and `nanyx build` write the binary format, which a browser loads, and WebAssembly text with `--wat`.
- Done: **the host.** `nanyx.playground` provides `Console` and `Random` on its own imports, `nanyx:playground` (see [Compiler internals](../compiler-internals/hosts)), and nothing else, so a program that needs `FileSystem`, `Env`, `Http` or `Async` is an error naming it.
- **The program runs in the module's start function.** A browser runs the start function while it instantiates the module, before the page can reach the exported memory, so the page can't read the bytes it is asked to print. For a browser the module has to export its entry for the page to call.
- **Exiting has to unwind.** The runtime's `proc_exit`, which a crash or a deferred error ends with, throws in the page, and the code that called the entry catches it and reads the exit code.
- **Reading input blocks**, and a page can't wait for input on its main thread, so the program runs in a worker ([#2151](https://github.com/nanyx-lang/nanyx/issues/2151)).

The backend also emits `return_call` for [tail calls](../compiler-internals/tail-calls), which needs Safari 18.2 or later; Chrome and Firefox have had it for longer.

## A browser host

A browser app is a different host, because the browser owns the main loop. The entry can't run to completion and exit: it returns, and the browser calls back into the module for each event. So the entry shape is a description of the app rather than a procedure, as in Elm:

```nanyx
module main

@entry
def main = (
  init = Model(count = 0)
  update = { model, message -> ... }
  view = { model -> ... }
)
```

The host provides contexts for what a page can do (`Dom`, `Fetch`, `Clock`, `Log`) and none for what it can't (`FileSystem`, `Env`). A library that requires `[FileSystem]` is then a compile error under the browser host, by the same check as for any other host.

Unlike the WASI worlds, this host's imports have no standard on the other side. The host package has to ship the JavaScript that implements them, and `nanyx build` has to write that file out beside the module.

# Serving HTTP

The HTTP host's entry is a request handler, an ordinary function:

```nanyx fragment
@entry
def main: [Log, Clock, Env] Request -> Response = { req ->
  match (req.method, req.path)
    | (#get, "/") -> Response.text(200, "Welcome")
    | (#get, #Path('/hello/{name}')(name)) -> Response.text(200, "Hello, {name}!")
    | _ -> Response.text(404, "Not Found")
}
```

**The handler is synchronous, and the host provides concurrency.** A page can't block, which is why the browser host
is Elm-style. A server can. The host runs many calls of the handler at once, one WebAssembly instance (or worker) per
request, as `wasmtime serve`, Spin and Fastly run components of the `wasi:http` world. Inside a request, an outbound
call or a query waits in that request's instance only. The handler requires `Async` for such a call, as any
function that waits does, and the host provides it ([Concurrency and crash boundaries](./concurrency-and-crash-boundaries)).

**What the host provides:** `Log`, `Clock`, `Env`, `Random` and `Http`, an outbound HTTP client, and later perhaps a
key-value store or a database. It doesn't provide `Console` or `FileSystem`, so a library that writes files is a
compile error under it. A handler may require fewer contexts than the host provides: a function type with fewer
contexts fits where one with more is expected, so a pure handler is fine.

**What the first version leaves out:**

- **Set-up and state shared between requests.** With an instance per request there is no memory shared between
  requests. When a program needs something made once, such as its configuration or a connection pool, the entry gains
  a second part. It is still one shape, and it mirrors the browser host's `init`:

  ```nanyx fragment
  @entry
  def main = (
    init = { -> loadConfig() }        -- runs once, and may require Env
    handle = { config, req -> ... }   -- runs for each request
  )
  ```

  The first version takes the handler alone. `init` comes when a program needs it.
- **Streamed bodies.** A body is a `string` at first. Streaming wants a lazy body, which depends on
  [lazy sequences](./lazy-sequences).

**Failures.** A trap in a handler, including a [deferred error](./deferred-errors), ends only that request. The host
answers it with a 500 and logs the message, and the server carries on.

**Routing, middleware and templates are libraries.** A router is a function from routes to a handler, so it produces
a `Request -> Response`. That is the next section's rule.

**What's built:** `Request` and `Response` in `nanyx.http.core`, with the methods as a tag union, headers as a list of
names and values, and the body as a string; the host, `nanyx.http`, on the machinery ConsoleHost builds; and serving
without WASI: `nanyx run` listens on the port the `settings` of the manifest's `host` give, and answers each request
with a fresh instance, which is enough for local development and for the [HTTP server cookbook](../cookbook/http-server)
([#1786](https://github.com/nanyx-lang/nanyx/issues/1786); see [How it's built](../compiler-internals/hosts#how-its-built)). The host provides
`Http` only, so far; what else it lacks is on [Files, HTTP and JSON](./files-http-json#http).

**What it still needs:** to serve on WASI, the `wasi:http` incoming-handler world. That is the largest piece: the
backend emits a WASI preview 1 core module today, and `wasi:http` is a preview 2 component.

# Libraries for a host

Some libraries only make sense with a certain kind of host: middleware for the web host, or a terminal UI for the
console host. Decided by the maintainer: such a library depends on what a host provides, its contexts and types,
never on the host itself.

- **Middleware** is a function from handler to handler, `[Log, Clock] (Request -> Response) -> (Request -> Response)`.
  It needs the `Request` and `Response` types and the `Log` and `Clock` contexts, not the host that serves them.
- **A terminal UI** needs raw keys, the terminal's size, cursor control and colour: a `Terminal` context, which the
  console host provides and a browser host doesn't. The library is ordinary `[Terminal]` code.

So each host is **two packages**:

- **An interface package** that declares the types and contexts: `Request`, `Response` and the HTTP host's own
  contexts; `Console`, `Env` and `Terminal` for the console. It is small, with no code beyond those declarations.
- **The host** that implements it, its `run` built on [foreign functions](./foreign-functions). It depends on its
  interface package.

A library depends only on the interface package. The capability check does the rest: a program whose host doesn't
provide `Terminal` gets a compile error naming `Terminal` at the library's call, as it does for `FileSystem` or
`Console` ([#1811](https://github.com/nanyx-lang/nanyx/issues/1811)).
The same interface can have more than one host, so middleware works under a local server, a serverless host or a
WASI one, and a third-party HTTP host is another implementation of the same interface, with every library already
written for it. This is the split WASI makes between an interface (WIT) and its implementation, and that WSGI makes
between a server and a framework.

The package index can show which hosts a library works with, with nothing written by its author: a package's
`api.json` lists the contexts it requires, and a host lists those it provides.

**Naming the pair.** Since an application names its host in its manifest more often than a library author names an
interface, the host could take the shorter name:

| | Host | Interface |
|---|---|---|
| Console | `nanyx.console` | `nanyx.console.core` |
| HTTP | `nanyx.http` | `nanyx.http.core` |

Rust's `futures` and `futures-core` follow this pattern. The alternative gives the interface the short name
(`nanyx.http` for the types, `nanyx.http.host` for the server), as Rust's `http` crate (types) beside `hyper` (a
server) does: a library author then writes the obvious name, and an application names the host it picked. This is an
open question below.

**What it needs from hosts.** Not every interface fits WASI. A terminal's raw mode and size aren't in WASI preview 1,
so the console host's `Terminal` would be built on imports the CLI's own runtime provides. A program using it then
runs under `nanyx run`, but not under another WASI runtime, which is accurate: it needs that host.

# Hosts are not frameworks

A host owns the *process lifecycle*. A framework is something that needs effects or produces an entry. Keeping these separate stops the ecosystem from fragmenting:

- The **HTTP host** listens on a socket, runs the request lifecycle, manages concurrency and shuts down gracefully. It expects a `Request -> Response`.
- A **web framework** provides routing, middleware and templating. It is a library that *produces* a `Request -> Response`.

The test is: **if it needs to own the main loop, it's a host; if it only needs effects, it's a library that requires contexts.** That way any framework runs on any HTTP host, and swapping hosts (local server, serverless, WASI) doesn't mean rewriting the application. Python's WSGI/ASGI and Ruby's Rack show how this split works out, and what happens without it.

# The test runner is a host

`nanyx test` runs the program under a test host. It discovers test functions instead of an `@entry`, and it provides fake or recording instances of the contexts the code under test requires. The same mechanism that lets production code run as a CLI or a web service lets it run in a test with no IO. That's a useful check that the design is general enough. [Testing](./testing) proposes how tests are written and run.

# Composing hosts

Real services often need more than one long-running activity, for example an HTTP server plus a background worker plus a scheduled job. Because a host is a function that takes an entry, a composite host is a function that takes a record of entries:

```nanyx
@entry
def main = (http = handler, worker = processQueue, nightly = Schedule.daily(at = "02:00", cleanup))
```

The composite host runs each part under the right sub-host and coordinates startup and shutdown. The open question is whether composition is a language-level concept, with every host composable, or something a dedicated `ServiceHost` provides. .NET's Generic Host, with `IHostedService`, takes the second approach.

# Prior art

- **Roc**: platforms and applications. This is the closest existing design. The platform owns `main` and all effects; the application is pure. Roc's platforms are written in a systems language, and making effects available to libraries in a platform-agnostic way was a long-running problem. Nanyx's contexts address that directly.
- **Elm**: `Browser.sandbox`, `Browser.element` and `Browser.application` are hosts that each require a different entry shape.
- **WASI**: worlds describe the imports and exports an embedder provides, which corresponds to a host's contexts and entry shape.
- **WSGI / ASGI / Rack / OWIN**: the split between server and framework.
- **.NET Generic Host**: host configuration, lifetime and composition of hosted services.

# Decided

- **A host is a function that takes the entry and runs it**, under `use`s of the contexts it provides. Checking an
  entry against a host is checking it against `run`'s parameter type, and a library that requires a context the host
  doesn't provide fails the same check. Built ([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)).
- **One entry shape per host.** The console host's is `() -> ()`. A program's arguments, environment and exit code
  come from an `Env` context, not from a second shape. Built ([#1246](https://github.com/nanyx-lang/nanyx/issues/1246)).
- **Hosts whose platform can't block are Elm-style for now.** The browser host's entry is `init`, `update` and `view`,
  and effects such as fetching and timers come back to `update` as messages. A host that can't suspend doesn't
  provide `Async`, so a program whose entry needs it is an error there.
- **A host that can suspend provides `Async`**, as it provides `Console`: its prelude exports it and its `run`
  requires it, and the entry's type says whether it needs it. The console and HTTP hosts do. Built
  ([#1889](https://github.com/nanyx-lang/nanyx/issues/1889), see
  [Concurrency and crash boundaries](./concurrency-and-crash-boundaries)).
- **Hosts are ordinary packages** that the compiler ships but doesn't treat specially
  ([Built-ins as packages](./built-ins-as-packages)), chosen with the manifest's `host`, `--host` or a script's
  `::host`. Built ([#1247](https://github.com/nanyx-lang/nanyx/issues/1247),
  [#1344](https://github.com/nanyx-lang/nanyx/issues/1344), [#1504](https://github.com/nanyx-lang/nanyx/issues/1504),
  [#1599](https://github.com/nanyx-lang/nanyx/issues/1599)). `nanyx.console` and its interface package
  `nanyx.console.core` are working names (see [Open questions](#open-questions)).

# Plan

Done: the console host written in Nanyx (#1245), `Env` (#1246), choosing a host (#1247), hosts on foreign functions
(#1424, #1425, #1426), and any package as a host, with `::host` and `::dependency` (#1344, #1504).

1. Done: **more contexts for the console host**: [`Random`](../stdlib/random)
   ([#1237](https://github.com/nanyx-lang/nanyx/issues/1237)), `FileSystem` ([Files, HTTP and JSON](./files-http-json), [#1783](https://github.com/nanyx-lang/nanyx/issues/1783)),
   `Http` ([#1785](https://github.com/nanyx-lang/nanyx/issues/1785)) and [`Clock`](../stdlib/clock), which the HTTP
   host provides too. `Clock` has no `sleep` yet, which belongs with `Async`.
2. **Console in a page**, for the [playground](./playground): what is left in "Console programs in a page" above;
   writing `.wasm` ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)) and the host itself are done
   ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)).
3. **The browser host**: `init`, `update` and `view`, with `Dom`, `Clock` and `Log`, and the JavaScript it ships
   beside the module. The [Nim demo](./nim-demo) is its first user.
4. **The test host**, with [Testing](./testing).
5. **The HTTP host** ([Serving HTTP](#serving-http)): serving from the CLI for local development is done (#1786); the
   `wasi:http` world is next.

# Open questions

- **Host configuration typing.** The `settings` of the manifest's `host` are checked against the settings the host
  declares in its manifest, with their defaults: a misspelt key is an error, and so is a value of another kind than its
  default. Checking them against a record type the host exports, as Nanyx types are checked, is still open.
- **Naming a host and its interface.** Does the host take the short name (`nanyx.console`, with `nanyx.console.core` for
  the interface), or the interface (`nanyx.http`, with `nanyx.http.host`)? See
  [Libraries for a host](#libraries-for-a-host). The console's are `nanyx.console` and `nanyx.console.core` for now:
  `Hosts.consolePackage` names the host, so that a rename is a small change.
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry it, and how does the build write it out?
