---
title: "Hosts"
description: "Hosts still to come (in a page, a browser, HTTP, tests), the design behind them, and how the compiler builds hosts"
order: 3
---

What works today, choosing a host, the console host, writing a host package, foreign functions and host memory, is
described in [Hosts](../advanced/hosts). This page keeps the hosts still to come, the design they follow, how the
compiler builds hosts, and the open questions.

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

Compare this with the current [HTTP server cookbook](../cookbook/http-server), where `main` runs under `Console` and calls `server.start(handler, port = 3000)` itself. With a host, the port comes from configuration, and listening, concurrency and graceful shutdown belong to the host.

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
| `ConsoleHost` | `() -> ()` | `Console & Env & FileSystem & Http`; `Clock` and `Random` as they are added |
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

## `ConsoleHost` in a page

A console program can run in a page unchanged, with the page standing in for the terminal. This is what a [playground](./playground) on the docs site needs. The host is still `ConsoleHost`; only the embedder differs. The module the backend emits is close to runnable there: it uses linear memory with its own allocator, and it imports only `fd_write`, `fd_read` and `proc_exit` from `wasi_snapshot_preview1`. A page can provide all of those in a few lines of JavaScript. Four things stand in the way:

- Done: **the toolchain writes `.wasm`** ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)). `nanyx wasm` and `nanyx build` write the binary format, which a browser loads, and WebAssembly text with `--wat`.
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

**What's built:** `Request` and `Response` in `nanyx.http.core`, with the methods as a tag union, headers as a list of
names and values, and the body as a string; the host, `nanyx.http`, on the machinery ConsoleHost builds; and serving
without WASI: `nanyx run` listens on the port the `settings` of the manifest's `host` give, and answers each request
with a fresh instance, which is enough for local development and for the [HTTP server cookbook](../cookbook/http-server)
([#1786](https://github.com/nanyx-lang/nanyx/issues/1786); how it's built is on
[Files, HTTP and JSON](./files-http-json#how-serving-is-built)). The host provides `Http` only, so far.

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
  and effects such as fetching and timers come back to `update` as messages. Nothing in the language suspends, so this
  can be built without async. Async is a separate design, for later.
- **Hosts are ordinary packages** that the compiler ships but doesn't treat specially
  ([Built-ins as packages](./built-ins-as-packages)), chosen with the manifest's `host`, `--host` or a script's
  `::host`. Built ([#1247](https://github.com/nanyx-lang/nanyx/issues/1247),
  [#1344](https://github.com/nanyx-lang/nanyx/issues/1344), [#1504](https://github.com/nanyx-lang/nanyx/issues/1504),
  [#1599](https://github.com/nanyx-lang/nanyx/issues/1599)). `nanyx.console` and its interface package
  `nanyx.console.core` are working names (see [Open questions](#open-questions)).

# Plan

Done: the console host written in Nanyx (#1245), `Env` (#1246), choosing a host (#1247), hosts on foreign functions
(#1424, #1425, #1426), and any package as a host, with `::host` and `::dependency` (#1344, #1504).

1. **More contexts for the console host**, as they are added: [`Random`](https://github.com/nanyx-lang/nanyx/issues/1237),
   then `Clock`. `FileSystem` is done ([Files, HTTP and JSON](./files-http-json), [#1783](https://github.com/nanyx-lang/nanyx/issues/1783)),
   but for reporting a library that requires it under a host that doesn't provide it
   ([#1811](https://github.com/nanyx-lang/nanyx/issues/1811)). `Http` is done too ([#1785](https://github.com/nanyx-lang/nanyx/issues/1785)).
2. **Console in a page**, for the [playground](./playground): the embedder changes in "`ConsoleHost` in a page"
   above; writing `.wasm` is done ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)).
3. **The browser host**: `init`, `update` and `view`, with `Dom`, `Clock` and `Log`, and the JavaScript it ships
   beside the module. The [Nim demo](./nim-demo) is its first user.
4. **The test host**, with [Testing](./testing).
5. **The HTTP host** ([Serving HTTP](#serving-http)): serving from the CLI for local development is done (#1786); the
   `wasi:http` world is next.

# How it's built

- **A host declares itself.** A host package's manifest has `host = (root = "...", prelude = "...")` in its `package`
  (`Manifest.checkHostDeclaration` checks it, `HostPackages.ofManifest` reads it into `Manifest.Declares`). `Host`
  (`Hosts.fs`) is what the compiler knows of one: its package, its root module, its prelude and its directory. The
  compiler keeps no table of hosts: `HostPackages.tryShipped` reads the manifest of one it ships, from its directory
  beside the compiler (`ShippedPackages`, see [Built-ins as packages](./built-ins-as-packages#how-shipped-packages-are-built)), and
  `HostPackages.console` is the console host, which a loose script runs under unless it names another. The host is imported as
  `nanyx.console/console.host` (`Host.Import`), and its prelude as `nanyx.console/console.prelude` (`Host.PreludeImport`).
- **A package chooses its host.** `Manifest.check` reads the manifest's `host`, `(package = "nanyx.console", version
  = "0.28.0")` or `(package = "acme.host", path = "../acme.host")`, into `Manifest.Host` (a `HostEntry`; none without a
  `host` field: a library, whose `@entry` `Attributes.applyAttributes` reports as `NYX0031`). A host the compiler
  doesn't ship, named without a path, is `NYX0023`, saying how to give its path; a host with both a path and a version
  is an error. Its `settings` are a record, checked against the settings the host declares, with their defaults, in its
  own manifest (`Host.Settings`, `HostSettings.errors`) once the host is read. `Package.ofManifest` reads the host's
  own manifest (`Package.hostIn`): its name must match, and it must declare itself a host. The host's package travels
  with the package's module table (`PackageModules.Host` and `PackageModules.HostPackage`), so every compile of a
  package's file, the language server's included, passes `@entry` to that host's `run`, and the host's imports resolve
  in its own package. `--host` on `run`, `build` and `check` replaces it with a host the compiler ships or the
  directory of one (`Package.withHost`); on a script it is a usage error.
- **A script chooses its host.** `Script.read` reads a script's `::host` and `::dependency` directives into the
  entries a manifest's `host` and `dependencies` would be (`Manifest.hostEntry`, `Manifest.dependencyEntries`), so they
  are checked as a manifest's are, with the same errors, at the word each is about. `Package.scriptModules` reads the packages
  they name, the console host when there is no `::host`, into a module table for the script's build
  (`PackageModules.Script`): it has no files of its own, so every file of the build in no package it depends on is the
  program's (`PackageModules.isProgramFile`), and its imports are found by path, but for an import of a dependency's
  public module, `acme.util/strings`. `Script.compileSourceReading` compiles a script in that build, for `nanyx run`
  and the language server alike. A script with neither directive compiles as before, in no build.
- **Which modules see the host.** A module of the program itself runs under the build's host: it sees the host's
  prelude and may declare the `@entry`. A module of a package the program depends on, of the host's own package, or
  of the standard library has no host (`checkModule` in `Library.fs`).
- **The host's prelude.** A module with a host imports its prelude after the standard library's, as an implicit import
  that the module's own imports win over. The contexts the prelude re-exports are those the host provides
  (`providesContexts` in `Library.fs`): importing the module that declares one, as the prelude does, binds the names
  of its members, so they are ambient (see below). An import that selects a context, as the prelude's
  `export nanyx.console.core/console (Console, ...)` does, selects its members too.
- **`@entry` calls the host's `run`.** `Attributes.applyAttributes` imports the host under an alias of its own
  (`__host`), so that it can't clash with the program's names, and appends `__host.run(main)` to the module as its
  start. The entry point passed to `run` is at the attribute's span, so a mismatch there is the entry not fitting the
  host: `NYX0237`, reported at `@entry`, with the type the host expects and the one the entry has.
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
  as an ambient context, because the host's prelude brings `Console`'s names in. A def that infers an ambient context takes its
  members as a def that writes it does: `InferModule` adds the contexts it inferred to the def's annotation for the
  backend (`[Console]` on its own, or after the parameterised contexts it was given), and the backend gives a function
  named as a value the contexts without parameters it requires (`CodeGen.carryPlainContexts`), so `run(main)` passes
  `main` its members rather than closing it over the module that declares `Console`. Nothing wires `Console` to the entry by name.
- **Scripts run under the host.** A module without an `@entry` whose top level has statements is a script, and its
  statements are its entry point: `Scripts.asEntry` makes them the body of a lambda, followed by `()`, and appends
  `__host.run({ ... })` to the module, importing the host (a script sees the host's prelude, as any program's module
  does).
  The lambda is not given `Console`'s members as a lambda passed to `run` would be
  (`Contexts.extendEnvForLambdaArgument`): its statements are the top level, and see the script's own names and
  ambient contexts as before. A script's value that needs a context, directly or through the functions and values it
  names (`def line = readLine()`), is inferred in a scope of its own (`ScriptValueScope`) that records what it
  needs. A lambda written in the value is not run by building it, so what it calls is its own type's, not the value's
  (`def handlers = (log = { m -> println(m) })` needs nothing). Once the module is inferred, those values (`InferState.ScriptEntryValues`) move into the entry point as local
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
  at the value. A script carries only the members of the host's contexts that it calls, as an entry program does
  (see `Env` below).
- **`Console` only comes from the host.** `nanyx.console.core/console` declares the context and `print`, `println`, `eprint` and
  `eprintln`, but no instance: the line reading lives in the console host alone. Importing a module whose contexts
  the host provides (those its prelude re-exports) binds the names of its plain contexts' members, `readLine` and the
  rest, each a symbol of its own (`Library.mergeImport`), so `Console` is still an ambient context: a script's
  statements and an unannotated def can call `readLine()`, and the def infers `Console`. A member of a context a def
  declares, or a `use` loads, shadows an imported function of the same name, and takes none of its contexts
  (`Contexts.contextRequirementsOf`): a def's own `println` member is not the prelude's `println`. A function named as a value carries it in its
  type (#1288), and an unannotated one carries those it was inferred to require, its own and those of the functions it
  calls (`Contexts.inferredAmbientContexts`), found once for each function it reaches however many paths reach it
  ([#1458](https://github.com/nanyx-lang/nanyx/issues/1458)). A def that writes `[c.Console]` for an aliased import is passed the members as `[Console]` is: the
  backend reads the layout of the context's own name (`CodeGen.layoutOf`), and a member called under the alias,
  `c.write`, is written by its own name for the backend (`ModuleLinking.writeMemberAliases`).
- **`Env`** is declared in `nanyx.console.core/env` (`args`, `variable`, `exit`), and `run` `use`s it beside `Console`, with
  `FileSystem` and `Http` (see below), so its parameter is `[Console, Env, FileSystem, Http] () -> a`; an entry that requires fewer
  contexts fits. The host's prelude re-exports
  `Env`, so its members are ambient names in every module of the program. Its members are functions of the host's own
  over WASI's `args_sizes_get` and `args_get`, `environ_sizes_get` and `environ_get`, and `proc_exit`, declared with
  `@extern`, with their buffers allocated with `nanyx/host.memory` (see
  [Foreign functions](./foreign-functions#whats-built)). `run` passes the members of both contexts to every entry,
  but the inliner inlines `run` and the entry into the start ([#1469](https://github.com/nanyx-lang/nanyx/issues/1469)),
  so a member nothing calls is never built, and a program contains only the members it calls and imports only what
  they call ([#1497](https://github.com/nanyx-lang/nanyx/issues/1497)): hello world imports `fd_write` alone. A
  program that reads `args()` imports no `environ_*` function, and one that reads only `variable` no `args_*` one
  ([#1766](https://github.com/nanyx-lang/nanyx/issues/1766)): `entriesOf`, which reads either list, is passed the
  list's two WASI functions, each wrapped in a lambda the inliner removes, rather than a tag it matches on, which
  built both lists' code into every caller. `nanyx run` passes what follows the
  file or package as the arguments, after the program's name, and the process's environment variables (see
  [`nanyx run`](../standard-tools/command-line#nanyx-run)); an `exit(0)` part-way is a clean end, which `--check-memory` doesn't
  count as a leak. A script's entry point requires only the plain contexts that are ambient in its module
  (`Scripts.withAmbientContextsOnly`): a statement that calls a function whose type is an error would otherwise leave it
  requiring all that `run` provides, `Env` too in a script that doesn't import it.
- **`FileSystem`** is declared in the shipped package `nanyx.fs`, which the console host depends on; its prelude
  re-exports `FileSystem`, with `Path` and `FsError`, the types its members name. Its members are written on WASI's
  file functions over the directories the runtime preopens (see
  [Files, HTTP and JSON](./files-http-json#how-the-file-system-is-built)). A program reaches its functions by
  importing `nanyx.fs`'s modules, with `::dependency nanyx.fs` or a manifest's `dependencies`.
- **`Http`** is declared in the shipped package `nanyx.http.core`, which the console host depends on; its prelude
  re-exports `Http` and `HttpError`, with `Request`, `Method` and `Response`. Its member is written on the
  runtime's own import `nanyx:http`, not on WASI, which has no networking (see
  [Files, HTTP and JSON](./files-http-json#how-the-client-is-built)).

# Open questions

- **Host configuration typing.** The `settings` of the manifest's `host` are checked against the settings the host
  declares in its manifest, with their defaults: a misspelt key is an error, and so is a value of another kind than its
  default. Checking them against a record type the host exports, as Nanyx types are checked, is still open.
- **Async and workflows.** Can a host change how the entry runs, for example by running it as a [workflow](../advanced/workflows) or on an event loop, without changing the entry's written type? [Concurrency and crash boundaries](./concurrency-and-crash-boundaries) proposes that a host provides an `Async` context or doesn't, and the entry's type says whether it needs one.
- **Naming a host and its interface.** Does the host take the short name (`nanyx.console`, with `nanyx.console.core` for
  the interface), or the interface (`nanyx.http`, with `nanyx.http.host`)? See
  [Libraries for a host](#libraries-for-a-host). The console's are `nanyx.console` and `nanyx.console.core` for now:
  `Hosts.consolePackage` names the host, so that a rename is a small change.
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry it, and how does the build write it out?
- **Calls that can't block, later.** The browser host starts Elm-style, with effects as messages. Whether contexts may
  one day suspend the program, so that a `fetch` reads like an ordinary call, is the async question above.
