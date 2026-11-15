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

```nanyx fragment
(
  package = (name = "my_server", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.http", version = "1.0.0", settings = (port = 3000))
)
```

`host` names the host's package and its version, and its `settings` are the host's configuration, passed to the host at startup. The CLI can override the host, for example `nanyx run --host nanyx.console/console.host`. Until any package can be a host, the only host a manifest can name is `nanyx.console`, the package the compiler ships with `nanyx.console/console.host` in it, and it takes no `settings` yet (`NYX0024`).

Once hosts are packages, there is no default host: a manifest with no `host` can't be run, and `nanyx new` writes one ([Built-ins as packages](./built-ins-as-packages#the-manifest)). Until then, a package with no `host` runs under the console host.
Loose script files, which run top-level statements without a manifest, always run under `ConsoleHost`.

# The entry shape is just a type

Each host takes exactly one entry shape. Under `ConsoleHost` the entry is `() -> ()`; a program that wants its arguments or to set its exit code requires the `Env` context from `nanyx/env`, which `ConsoleHost` provides (`args()`, `variable(name)`, `exit(code)`). Under an HTTP host the entry *is* the handler:

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
| `ConsoleHost` | `() -> ()` | `Console & Env`; `FileSystem`, `Clock` and `Random` as they are added |
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

```nanyx fragment
module console.host

import (
  nanyx/console as (Console, IoError)
  nanyx/host.runtime
)

export def run: [HostRuntime] ([Console] () -> a) -> () = { entry ->
  use Console(
    write = { text -> writeTo(1, text) }
    writeError = { text -> writeTo(2, text) }
    readLine = { _ -> readLineAfter([]) }
    readToEnd = { _ -> #some(readToEndAfter([])) }
    flush = { _ -> () }
  )

  ignore(entry())
}
```

That is `nanyx.console/console.host` as it is built (`packages/nanyx.console/src/console.host.nyx`), less the helpers that read a line and
the `use` of `Env` (see [What's built](#whats-built)).

`HostRuntime` is a context declared `protected` (see [nominal and protected types](../advanced/nominal-and-protected-types))
that marks the host: only code that has it can call the [foreign functions](./foreign-functions) the embedder provides
(raw IO, the process and, on the WASM backend, the module's imports) and reach linear memory. The toolchain provides
`HostRuntime` only to the module named as the host. Libraries can't require it, so they can't bypass
the capabilities the host chose to provide.

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
  serve without WASI: `nanyx run` listens on a port (from the `settings` of the manifest's `host`) and calls the handler for
  each request, which is enough for local development and for the [HTTP server cookbook](../cookbook/http-server).

# Libraries for a host

Some libraries only make sense with a certain kind of host: middleware for the web host, or a terminal UI for the
console host. Decided by the maintainer: such a library depends on what a host provides, its contexts and types,
never on the host itself.

- **Middleware** is a function from handler to handler, `[Log & Clock] (Request -> Response) -> (Request -> Response)`.
  It needs the `Request` and `Response` types and the `Log` and `Clock` contexts, not the host that serves them.
- **A terminal UI** needs raw keys, the terminal's size, cursor control and colour: a `Terminal` context, which the
  console host provides and a browser host doesn't. The library is ordinary `[Terminal]` code.

So each host is **two packages**:

- **An interface package** that declares the types and contexts: `Request`, `Response` and the HTTP host's own
  contexts; `Console`, `Env` and `Terminal` for the console. It is small, with no code beyond those declarations.
- **The host** that implements it, its `run` built on [foreign functions](./foreign-functions). It depends on its
  interface package.

A library depends only on the interface package. The capability check does the rest: a program whose host doesn't
provide `Terminal` gets a compile error naming `Terminal` at the library's call, as it does for `FileSystem` today.
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
- **Hosts are ordinary packages** that the compiler ships but doesn't treat specially
  ([Built-ins as packages](./built-ins-as-packages)): `nanyx.console`, with its module `console.host`, first, then a
  browser host. They are chosen with the manifest's `host` field or `--host` on the CLI. The console host ships with
  the compiler as a source directory already; the compiler still keeps a table of hosts until any package can be one
  ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)).
- **ConsoleHost written in Nanyx comes first.** It replaces the compiler's hard-coded entry wiring with no change for
  users, and builds what every other host needs.

# Plan

1. **ConsoleHost in Nanyx** ([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)): the console host's `run`
   takes the entry, the compiler checks `@entry` against it and starts the program with `run(entry)`, and
   `HostRuntime` is provided only to the host. Done: the host's `Console` reaches every entry and every script, and is
   the only `Console` a program has (see [What's built](#whats-built)).
2. **`Env`** ([#1246](https://github.com/nanyx-lang/nanyx/issues/1246)): arguments, environment variables and the
   exit code, provided by ConsoleHost. Done (see [What's built](#whats-built)).
3. **Choosing a host** ([#1247](https://github.com/nanyx-lang/nanyx/issues/1247)): `host` in the manifest and
   `--host` on the CLI, with errors that name the host. Done (see [What's built](#whats-built)).
4. **More contexts for ConsoleHost**, as they are added: [`Random`](https://github.com/nanyx-lang/nanyx/issues/1237),
   then `Clock` and `FileSystem`.
4a. **Hosts on foreign functions** ([Foreign functions](./foreign-functions)): `@extern` imports that only code with
   `HostRuntime` can call ([#1424](https://github.com/nanyx-lang/nanyx/issues/1424), done), host access to linear memory
   ([#1425](https://github.com/nanyx-lang/nanyx/issues/1425), done), and the console host written on them with no compiler
   intrinsics ([#1426](https://github.com/nanyx-lang/nanyx/issues/1426), done), so it can ship as a package.
5. **Console in a page**, for the [playground](./playground): the embedder changes in "`ConsoleHost` in a page"
   above, starting with writing `.wasm` ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)).
6. **The browser host**: `init`, `update` and `view`, with `Dom`, `Clock` and `Log`, and the JavaScript it ships
   beside the module. The [Nim demo](./nim-demo) is its first user.
7. **The test host**, with [Testing](./testing).
8. **The HTTP host** ([Serving HTTP](#serving-http)): `nanyx/http`'s `Request` and `Response`, serving from the CLI
   for local development, then the `wasi:http` world.

# What's built

- **The host is a module the compiler resolves.** `Hosts.fs` describes each host the compiler ships (`Host`: the
  package a manifest names it by, `nanyx.console`, its module, `console.host`, and the modules of the contexts it
  provides) and names the default, `Hosts.console`. The package is a source directory beside the compiler
  (`ShippedPackages`, see [Built-ins as packages](./built-ins-as-packages#whats-built)), and the host is imported from
  it as `nanyx.console/console.host` (`Host.Import`). Choosing another host is choosing another `Host` there:
  `Hosts.tryFind` gives the one an import (`nanyx.console/console.host`) is, or an error that names it.
- **A package chooses its host.** `Manifest.check` reads the manifest's `host`, `(package = "nanyx.console", version
  = "0.28.0")`, into `Manifest.Host` (the console host without a `host` field). A package that isn't a host is
  `NYX0023`, and `settings` is `NYX0024`, since no host takes configuration yet.
  `--host` on `run`, `build` and `check` replaces it (`Package.withHost`); on a script it is a usage error. The host
  travels with the package's module table (`PackageModules.Host`), so every compile of a package's file, the language
  server's included, passes `@entry` to that host's `run`; a file in no package runs under the console host.
- **`@entry` calls the host's `run`.** `Attributes.applyAttributes` imports the host under an alias of its own
  (`__host`), so that it can't clash with the program's names, imports the modules of the contexts the host provides
  (`nanyx/console`, so that `println` is in scope), and appends `__host.run(main)` to the module as its start. The
  entry point passed to `run` is at the attribute's span, so a mismatch there is the entry not fitting the host:
  `NYX0237`, reported at `@entry`, with the type the host expects and the one the entry has.
- **`run` ignores what the entry returns.** Its parameter is `[Console] () -> a`, not `() -> ()`, because an entry
  whose block ends in an expression (`dbg(x)`) returned a value that the old `main()` call dropped.
- **`HostRuntime`** is declared in `nanyx/host.runtime`, a context with no members: the capability that lets code
  call [foreign functions](./foreign-functions) and use `nanyx/host.memory`. With no members, a function that requires
  it is passed nothing, and the type checker treats it as available wherever it is required, so `Hosts.runtimeErrors`
  reports `NYX0238` in any module but a host, `nanyx/host.runtime` and `nanyx/host.memory` by what each call calls: at
  each top-level def that requires it, as its written contexts say or a call in it does, other than a foreign function,
  which any module may declare, and at each call at the top level that requires it, other than the call of `run` that
  `@entry` makes. A module that calls a host's `run` itself needs `HostRuntime` for it, so that is an error too.
- **Where the entry's `Console` comes from.** Every entry is passed the members of the `Console` that `run` `use`s, so
  it reads and writes through the host. An entry that doesn't write its type, which is most programs, infers `Console`
  as an ambient context, because the module imports `nanyx/console`. A def that infers an ambient context takes its
  members as a def that writes it does: `InferModule` adds the contexts it inferred to the def's annotation for the
  backend (`[Console]` on its own, or after the parameterised contexts it was given), and the backend gives a function
  named as a value the contexts without parameters it requires (`CodeGen.carryPlainContexts`), so `run(main)` passes
  `main` its members rather than closing it over `nanyx/console`'s. Nothing wires `Console` to the entry by name.
- **Scripts run under the host.** A module without an `@entry` whose top level has statements is a script, and its
  statements are its entry point: `Scripts.asEntry` makes them the body of a lambda, followed by `()`, and appends
  `__host.run({ ... })` to the module, importing only the host (a script imports `nanyx/console` itself if it prints).
  The lambda is not given `Console`'s members as a lambda passed to `run` would be
  (`Contexts.extendEnvForLambdaArgument`): its statements are the top level, and see the script's own names and
  ambient contexts as before. A script's value that needs a context, directly or through the functions and values it
  names (`def line = readLine()`), is inferred in a scope of its own (`ScriptValueScope`) that records what it
  needs; once the module is inferred, those values (`InferState.ScriptEntryValues`) move into the entry point as local
  defs, in the order written, so each is computed once
  ([#1257](https://github.com/nanyx-lang/nanyx/issues/1257)). A value that needs no context stays at the top level,
  where a function can name it, and is built on its first read
  ([Memory management](./memory-management#core-ir)). A function that names a value that moved, or a statement that reads one before its def,
  is `NYX0239`. The ambient contexts the statements and the moved values need through unannotated functions are
  only known once the module is inferred, so the lambda's type is given them then (`InferModule.withEntryContexts`),
  and the host passes their members. Once typed, the entry point is a def of its own, `def __script: [Console] = { ... }`,
  which `__host.run(__script)` names (`Scripts.asDefinition`), so a lambda in a statement captures its members as a
  lambda in any def does. Statements beside an `@entry` are a script entry too, run before the `@entry`'s, as they ran
  at the top level. A value that needs a context in a module with no statements, which nothing would run, is an error
  at the value. Every script carries the host's `Console`, about 24 KB of WebAssembly text for hello world, as an
  entry program does.
- **`Console` only comes from the host.** `nanyx/console` declares the context and `print`, `println`, `eprint` and
  `eprintln`, but no instance: the line reading lives in the console host alone. Importing a module a host
  provides (`Hosts.providesContexts`) binds the names of its plain contexts' members, `readLine` and the rest, each a
  symbol of its own (`Library.mergeImport`), so `Console` is still an ambient context: a script's statements and an
  unannotated def can call `readLine()`, and the def infers `Console`. A function named as a value carries it in its
  type (#1288). A def that writes `[c.Console]` for an aliased import is passed the members as `[Console]` is: the
  backend reads the layout of the context's own name (`CodeGen.layoutOf`), and a member called under the alias,
  `c.write`, is written by its own name for the backend (`ModuleLinking.writeMemberAliases`).
- **`Env`** is declared in `nanyx/env` (`args`, `variable`, `exit`), and `run` `use`s it beside `Console`, so its
  parameter is `[Console & Env] () -> a`; an entry that requires fewer contexts fits. `nanyx/env` is one of the
  host's `ProvidedModules`, so its members are ambient names where it is imported, but not one of its
  `ImplicitModules`: a program imports it itself. Its members are functions of the host's own over WASI's
  `args_sizes_get` and `args_get`, `environ_sizes_get` and `environ_get`, and `proc_exit`, declared with `@extern`, with
  their buffers allocated with `nanyx/host.memory` (see [Foreign functions](./foreign-functions#the-console-host)). A
  program that doesn't call them doesn't contain them, since a module emits only the functions its
  start reaches ([#1329](https://github.com/nanyx-lang/nanyx/issues/1329)). `nanyx run` passes what follows the
  file or package as the arguments, after the program's name, and the process's environment variables (see
  [The `nanyx` command](./command-line#usage)); an `exit(0)` part-way is a clean end, which `--check-memory` doesn't
  count as a leak. A script's entry point requires only the plain contexts that are ambient in its module
  (`Scripts.withAmbientContextsOnly`): a statement that calls a function whose type is an error would otherwise leave it
  requiring all that `run` provides, `Env` too in a script that doesn't import it.

# Open questions

- **Host configuration typing.** The `settings` of the manifest's `host` should be checked against a record type the host exports, so that misspelt keys are errors.
- **Async and workflows.** Can a host change how the entry runs, for example by running it as a [workflow](../advanced/workflows) or on an event loop, without changing the entry's written type?
- **Naming a host and its interface.** Does the host take the short name (`nanyx.console`, with `nanyx.console.core` for
  the interface), or the interface (`nanyx.http`, with `nanyx.http.host`)? See
  [Libraries for a host](#libraries-for-a-host).
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry it, and how does the build write it out?
- **Calls that can't block, later.** The browser host starts Elm-style, with effects as messages. Whether contexts may
  one day suspend the program, so that a `fetch` reads like an ordinary call, is the async question above.
- **Running scripts under another host.** Should a loose script be able to pick a non-default host, for example with a directive at the top of the file?
