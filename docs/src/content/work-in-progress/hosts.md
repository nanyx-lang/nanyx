---
title: "Hosts"
description: "The shell a Nanyx program runs in: its entry shape, its contexts and its lifecycle"
order: 3
---

This page proposes **hosts**: a first-class description of the environment a Nanyx program runs in. A command-line tool, an HTTP service, a browser app and a test run are all the same program model running under different hosts.

Work has started. The first decisions are under [Decided](#decided), and the [Plan](#plan) begins with ConsoleHost
written in Nanyx.

# Motivation

Today the entry function has one special case built in: it [runs in `Console`](../advanced/contexts#console-by-default). That is already an implicit host, with a single hard-coded answer to three questions:

1. What shape must the entry function have?
2. Which contexts are available to it?
3. Who owns the process: the main loop, concurrency, signals, shutdown and the exit code?

Different kinds of program need different answers. An HTTP service wants its entry to be a `Request -> Response` handler and shouldn't have to write its own accept loop. A sandboxed plugin should get *fewer* capabilities than a CLI tool. A test run wants fake or recording implementations of the same contexts the real program uses.

Hosts make these answers explicit and swappable, and let the compiler check them.

# What a host is

A host is a package that provides three things:

- **An entry shape.** This is the type the program's `@entry` must have.
- **A set of contexts.** These are the capabilities the host loads around the entry.
- **A lifecycle.** This is the loop that calls the entry and handles process concerns.

If a program doesn't name a host, it runs under the default `ConsoleHost`, which keeps today's behaviour:

```nanyx
module main

@entry
def main = {
  println("Hello, World!")
}
```

# Choosing a host

A program's host is a deployment concern. It decides which runtime gets linked, so it's declared in the `.nyxp` manifest rather than in source:

```toml
[package]
name = "my_server"
version = "0.1.0"
sources = ["src/**/*.nyx"]

[dependencies]
"nanyx.http" = "1.0.0"

[host]
use = "nanyx.http/host"
port = 3000
```

Keys other than `use` are host configuration, passed to the host at startup. The CLI can override the host, for example `nanyx run --host nanyx/console/host`. Until packages resolve, the hosts that can be chosen are the standard library's, such as `nanyx/console/host`.

Loose script files, which run top-level statements without a manifest, always run under `ConsoleHost`.

# The entry shape is just a type

Each host takes exactly one entry shape. Under `ConsoleHost` the entry is `() -> ()`; a program that wants its arguments or to set its exit code requires the `Env` context, which `ConsoleHost` provides (`Env.args()`, `Env.exit(code)`). Under an HTTP host the entry *is* the handler:

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

Compare this with the current [HTTP server cookbook](../cookbook/http-server), where `main` runs under `Console` and calls `server.start(handler, port = 3000)` itself. With a host, the port comes from configuration, and listening, concurrency and graceful shutdown belong to the host.

If the entry has the wrong shape, the error names the host:

```
error: the entry `main` doesn't match the host `nanyx.http/host`
  expected: [Log & Clock] Request -> Response
  found:    [Console] () -> ()
```

# Hosts provide contexts

A host provides the contexts the program's entry may require, and only those. This is where hosts and [contexts](../advanced/contexts) fit together:

| Host | Entry shape | Provides |
| --- | --- | --- |
| `ConsoleHost` | `() -> ()` | `Console & Env` at first; `FileSystem`, `Clock` and `Random` as they are added |
| HTTP host | `Request -> Response` | `Log & Clock & Env & Random & Http` (see [Serving HTTP](#serving-http)) |
| Browser host | a record of `init`, `update` and `view` | `Dom & Fetch & Clock & Log` |
| Plugin / sandbox host | host-defined | only what the embedder grants |
| Test host | test functions | fakes or recorders of the above |

Libraries stay host-agnostic because they never assume a host. They declare what they need, such as `[FileSystem]`, and the compiler checks that the chosen host provides it:

```
error: `config.load` requires context `FileSystem`, which `nanyx.http/host` does not provide
```

This gives capability security as a side effect. A host that doesn't provide `FileSystem` guarantees the program can't touch the file system. That includes every library the program uses.

# Defining a host

A host is mostly ordinary Nanyx. Its core is a function that takes the program's entry and runs it. The entry's type is the host's parameter type, so checking the entry against the host is ordinary type checking:

```nanyx
module nanyx/console/host

export def run
  : [$HostRuntime] ([Console & Env] () -> ()) -> ()
  = { entry ->
    use Console(write = runtime.writeStdout, writeError = runtime.writeStderr, ...)
    use Env(args = runtime.args, variable = runtime.variable, exit = runtime.exit)

    entry()
  }
```

`$HostRuntime` is a [nominal context](../advanced/nominal-and-protected-types) that exposes the privileged layer: raw IO, sockets, the process and, on the WASM backend, the module's imports. The toolchain provides `$HostRuntime` only to the module named as the host. Libraries can't require it, so they can't bypass the capabilities the host chose to provide. (Once [protected types](./protected-types) land, it is written `HostRuntime`, declared `protected`.)

A host takes one entry shape. Arguments and the exit code, which would otherwise call for a second shape, come through a context instead.

On the WASM backend, a host maps directly onto a set of imports and exports: the "world" the module is instantiated in. The WASI `wasi:cli` and `wasi:http` worlds are natural targets for `ConsoleHost` and an HTTP host.

# Running in a browser

A browser can be the embedder for two different hosts. Neither works today.

## `ConsoleHost` in a page

A console program can run in a page unchanged, with the page standing in for the terminal. This is what a [playground](./playground) on the docs site needs. The host is still `ConsoleHost`; only the embedder differs. The module the backend emits is close to runnable there: it uses linear memory with its own allocator, and it imports only `fd_write`, `fd_read` and `proc_exit` from `wasi_snapshot_preview1`. A page can provide all of those in a few lines of JavaScript. Four things stand in the way:

- **The toolchain writes WebAssembly text.** `nanyx wasm` and `nanyx build` produce `.wat`, and a browser loads only the binary format. The toolchain needs to write `.wasm`.
- **The program runs in the module's start function.** A browser runs the start function while it instantiates the module, before the page can reach the exported memory, so the page's `fd_write` can't read the bytes it is asked to print. For a browser the module has to export its entry for the page to call, or import its memory.
- **`proc_exit` has to unwind.** The page's `proc_exit` throws, and the code that called the entry catches it and reads the exit code.
- **Reading stdin blocks.** `fd_read` returns its bytes before the program carries on, and a page can't wait for input on its main thread. The program would have to run in a worker, or `Console` input would have to be unavailable.

The backend also emits `return_call` for [tail calls](./tail-calls), which needs Safari 18.2 or later; Chrome and Firefox have had it for longer.

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
def main: [Log & Clock & Env] Request -> Response = { req ->
  match (req.method, req.path)
    | (#get, "/") -> Response.text(200, "Welcome")
    | (#get, #Path('/hello/{name}')(name)) -> Response.text(200, "Hello, {name}!")
    | _ -> Response.text(404, "Not Found")
}
```

**The handler is synchronous, and the host provides concurrency.** A page can't block, which is why the browser host
is Elm-style. A server can. The host runs many calls of the handler at once, one WebAssembly instance (or worker) per
request, as `wasmtime serve`, Spin and Fastly run components of the `wasi:http` world. Inside a request, an outbound
call or a query blocks only that request's instance. So the handler needs no async, and the
[decision to leave async for later](#decided) holds for servers too.

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

**What it needs:**

- `Request` and `Response` in a `nanyx/http` module, with the methods as a tag union, headers as a map, and the body
  as a string.
- The host itself, on the machinery ConsoleHost builds ([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)).
- Something to serve with. On WASI, that's the `wasi:http` incoming-handler world. That is the largest piece: the
  backend emits a WASI preview 1 core module today, and `wasi:http` is a preview 2 component. Before that, the CLI can
  serve without WASI: `nanyx run` listens on a port (from the manifest's `[host]` table) and calls the handler for
  each request, which is enough for local development and for the [HTTP server cookbook](../cookbook/http-server).

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
def main = (
  http = handler
  worker = processQueue
  nightly = Schedule.daily(at = "02:00", cleanup)
)
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
  doesn't provide fails the same check.
- **One entry shape per host.** `ConsoleHost`'s is `() -> ()`. A program's arguments, environment and exit code come
  from an `Env` context, not from a second shape.
- **Hosts whose platform can't block are Elm-style for now.** The browser host's entry is `init`, `update` and `view`,
  and effects such as fetching and timers come back to `update` as messages. Nothing in the language suspends, so this
  can be built without async. Async is a separate design, for later.
- **Hosts live in the standard library until packages resolve**: `nanyx/console/host` first, then
  `nanyx/browser/host`. They are chosen with a `[host]` table in the manifest or `--host` on the CLI.
- **ConsoleHost written in Nanyx comes first.** It replaces the compiler's hard-coded entry wiring with no change for
  users, and builds what every other host needs.

# Plan

1. **ConsoleHost in Nanyx** ([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)): `nanyx/console/host`'s `run`
   takes the entry, the compiler checks `@entry` against it and starts the program with `run(entry)`, and
   `HostRuntime` is provided only to the host.
2. **`Env`** ([#1246](https://github.com/nanyx-lang/nanyx/issues/1246)): arguments, environment variables and the
   exit code, provided by ConsoleHost.
3. **Choosing a host** ([#1247](https://github.com/nanyx-lang/nanyx/issues/1247)): `[host]` in the manifest and
   `--host` on the CLI, with errors that name the host.
4. **More contexts for ConsoleHost**, as they are added: [`Random`](https://github.com/nanyx-lang/nanyx/issues/1237),
   then `Clock` and `FileSystem`.
5. **Console in a page**, for the [playground](./playground): the embedder changes in "`ConsoleHost` in a page"
   above, starting with writing `.wasm` ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)).
6. **The browser host**: `init`, `update` and `view`, with `Dom`, `Clock` and `Log`, and the JavaScript it ships
   beside the module. The [Nim demo](./nim-demo) is its first user.
7. **The test host**, with [Testing](./testing).
8. **The HTTP host** ([Serving HTTP](#serving-http)): `nanyx/http`'s `Request` and `Response`, serving from the CLI
   for local development, then the `wasi:http` world.

# Open questions

- **Host configuration typing.** The `[host]` table in the manifest should be checked against a record type the host exports, so that misspelt keys are errors.
- **Async and workflows.** Can a host change how the entry runs, for example by running it as a [workflow](../advanced/workflows) or on an event loop, without changing the entry's written type?
- **Libraries that require a host.** Should a library be able to say "I only make sense under an HTTP host", or is requiring the right contexts always enough?
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry it, and how does the build write it out?
- **Calls that can't block, later.** The browser host starts Elm-style, with effects as messages. Whether contexts may
  one day suspend the program, so that a `fetch` reads like an ordinary call, is the async question above.
- **Running scripts under another host.** Should a loose script be able to pick a non-default host, for example with a directive at the top of the file?
