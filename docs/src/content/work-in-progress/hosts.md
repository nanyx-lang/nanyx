---
title: "Hosts"
description: "The shell a Nanyx program runs in: its entry shape, its contexts and its lifecycle"
order: 3
---

This page proposes **hosts**: a first-class description of the environment a Nanyx program runs in. A command-line tool, an HTTP service, a browser app and a test run are all the same program model running under different hosts.

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

Keys other than `use` are host configuration, passed to the host at startup. The CLI can override the host, for example `nanyx run --host nanyx/console`.

Loose script files, which run top-level statements without a manifest, always run under `ConsoleHost`.

# The entry shape is just a type

Under `ConsoleHost` the entry is `() -> ()`, or optionally `list(string) -> int` to receive arguments and return an exit code. Under an HTTP host the entry *is* the handler:

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
| `ConsoleHost` | `() -> ()` or `list(string) -> int` | `Console & FileSystem & Env & Clock` |
| HTTP host | `Request -> Response` | `Log & Clock & Env` |
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
  : [$HostRuntime] ([Console & FileSystem & Env & Clock] list(string) -> int) -> ()
  = { entry ->
    use Console(write = runtime.writeStdout, writeError = runtime.writeStderr, ...)
    use FileSystem(...)
    use Env(...)
    use Clock(...)

    def code = entry(runtime.args())
    runtime.exit(code)
  }
```

`$HostRuntime` is a [nominal context](../advanced/nominal-and-protected-types) that exposes the privileged layer: raw IO, sockets, the process and, on the WASM backend, the module's imports. The toolchain provides `$HostRuntime` only to the package named as the host. Libraries can't require it, so they can't bypass the capabilities the host chose to provide.

The compiler accepts either form of entry for `ConsoleHost` (`() -> ()` and `list(string) -> int`), so a host needs a way to accept more than one entry shape. One option is a [tag union](../tour/tags) of the accepted shapes. Another is to let a host export more than one `run` overload.

On the WASM backend, a host maps directly onto a set of imports and exports: the "world" the module is instantiated in. The WASI `wasi:cli` and `wasi:http` worlds are natural targets for `ConsoleHost` and an HTTP host.

# Running in a browser

A browser can be the embedder for two different hosts. Neither works today.

## `ConsoleHost` in a page

A console program can run in a page unchanged, with the page standing in for the terminal. This is what a playground on the docs site needs. The host is still `ConsoleHost`; only the embedder differs. The module the backend emits is close to runnable there: it uses linear memory with its own allocator, and it imports only `fd_write`, `fd_read` and `proc_exit` from `wasi_snapshot_preview1`, plus `env.dbg_tag_*` for `dbg` of a tag. A page can provide all of those in a few lines of JavaScript. Four things stand in the way:

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

# Open questions

- **Accepting more than one entry shape.** Should a host be able to accept several shapes, as `ConsoleHost` does? If so, do we use tag unions, overloads or a dedicated declaration form?
- **Host configuration typing.** The `[host]` table in the manifest should be checked against a record type the host exports, so that misspelt keys are errors.
- **Async and workflows.** Can a host change how the entry runs, for example by running it as a [workflow](../advanced/workflows) or on an event loop, without changing the entry's written type?
- **Libraries that require a host.** Should a library be able to say "I only make sense under an HTTP host", or is requiring the right contexts always enough?
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry it, and how does the build write it out?
- **Calls that can't block.** A browser's `fetch` returns a promise, and a page can't wait on it. Does the browser host's entry only ever return descriptions of effects, as Elm's does, or do its contexts suspend the program (see the async question above)?
- **Running scripts under another host.** Should a loose script be able to pick a non-default host, for example with a directive at the top of the file?
