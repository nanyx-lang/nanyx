---
title: "Hosts"
description: "Hosts still to come (a browser host, a test host, composite hosts), what the HTTP host still needs, and the open questions"
order: 3
---

The console, HTTP and playground hosts are built. How to choose, use and write a host is in [Hosts](../advanced/hosts),
and how the compiler builds hosts, and why they have the shape they do, is in [Hosts](../deep-dives/hosts) in the Deep
dives section. This page keeps the hosts still to come, what the built ones still lack, and the open questions.

# Hosts still to come

Each host takes one entry shape and provides a set of contexts:

| Host | Entry shape | Provides |
| --- | --- | --- |
| Browser host | a record of `init`, `update` and `view` | `Dom & Fetch & Clock & Log` |
| Plugin / sandbox host | host-defined | only what the embedder grants |
| Test host | test functions | fakes or recorders of the contexts the code under test requires |

# Running in a browser

A console program already runs in a page, under the [playground host](../advanced/hosts#the-playground-host). A
browser app needs a host of its own.

## A browser host

A browser app is a different host, because the browser owns the main loop. The entry can't run to completion and exit:
it returns, and the browser calls back into the module for each event. So the entry shape is a description of the app
rather than a procedure, as in Elm:

```nanyx fragment
module main

@entry
def main = (
  init = Model(count = 0)
  update = { model, message -> ... }
  view = { model -> ... }
)
```

Decided: **a host whose platform can't block is Elm-style for now.** Effects such as fetching and timers come back to
`update` as messages. A host that can't suspend doesn't provide `Async`, so a program whose entry needs it is an error
there.

The host provides contexts for what a page can do (`Dom`, `Fetch`, `Clock`, `Log`) and none for what it can't
(`FileSystem`, `Env`). A library that requires `[FileSystem]` is then a compile error under the browser host, by the
same check as for any other host. It provides `Random` from `crypto.getRandomValues`.

Unlike the WASI worlds, this host's imports have no standard on the other side. The host package has to ship the
JavaScript that implements them, and `nanyx build` has to write that file out beside the module (see
[Open questions](#open-questions)).

# Serving HTTP

The [HTTP host](../advanced/hosts#the-http-host) is built: its entry is a `Request -> Response` handler, and
`nanyx run` serves it with a fresh instance per request. What it still needs:

- **More contexts.** It provides `Http`, `Async`, `Random` and `Clock`. `Log` and `Env` are planned, and later perhaps
  a key-value store or a database. `Log` doesn't exist yet. `Env`'s members are written in the console host's root
  module; moved into the [host library](../advanced/hosts#host-libraries) `nanyx.wasi`, they could be shared. It won't
  provide `Console` or `FileSystem`.
- **Route patterns.** A `#Path` pattern would match a path and bind its parts. It isn't built; the
  [HTTP server cookbook](../cookbook/http-server) splits the path and matches the parts with a list pattern:

  ```nanyx fragment
  @entry
  def main: [Log, Clock, Env] Request -> Response = { req ->
    match (req.method, req.path)
      | (#get, "/") -> Response.text(200, "Welcome")
      | (#get, #Path('/hello/{name}')(name)) -> Response.text(200, "Hello, {name}!")
      | _ -> Response.text(404, "Not Found")
  }
  ```

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

  The host takes the handler alone until a program needs `init`.
- **Streamed bodies.** A body is a `string`. Streaming wants a lazy body, which depends on
  [lazy sequences](./lazy-sequences).
- **Serving more widely**: a server bound to other addresses than `localhost`.

Routing, middleware and templates are libraries, not part of the host: a router is a function from routes to a
handler, so it produces a `Request -> Response` ([Hosts are not frameworks](../deep-dives/hosts#hosts-are-not-frameworks)).

# Libraries for a host

A library for a kind of host depends on the host's interface package, never on the host
([Interface packages](../deep-dives/hosts#interface-packages)). What is still to come:

- **A terminal UI** needs raw keys, the terminal's size, cursor control and colour: a `Terminal` context, declared in
  `nanyx.cli`, which the console host provides and a browser host doesn't. The library is ordinary
  `[Terminal]` code, and a program whose host doesn't provide `Terminal` gets a compile error naming it at the
  library's call, as it does for `FileSystem`. A terminal's raw mode and size aren't in WASI, so the console host's
  `Terminal` would be built on imports the CLI's own runtime provides. A program using it then runs under
  `nanyx run`, but not under another WASI runtime, which is accurate: it needs that host.
- **Which hosts a library works with**, shown by the package index with nothing written by its author: a package's
  `api.json` lists the contexts it requires, and a host lists those it provides.

# The test runner is a host

`nanyx test` runs the program under a test host. It discovers test functions instead of an `@entry`, and it provides
fake or recording instances of the contexts the code under test requires. The same mechanism that lets production
code run as a CLI or a web service lets it run in a test with no IO. That's a useful check that the design is general
enough. [Testing](./testing) proposes how tests are written and run.

# Composing hosts

Real services often need more than one long-running activity, for example an HTTP server plus a background worker plus
a scheduled job. Because a host is a function that takes an entry, a composite host is a function that takes a record
of entries:

```nanyx fragment
@entry
def main = (http = handler, worker = processQueue, nightly = Schedule.daily(at = "02:00", cleanup))
```

The composite host runs each part under the right sub-host and coordinates startup and shutdown. The open question is
whether composition is a language-level concept, with every host composable, or something a dedicated `ServiceHost`
provides. .NET's Generic Host, with `IHostedService`, takes the second approach.

# Plan

1. **The browser host**: `init`, `update` and `view`, with `Dom`, `Clock` and `Log`, and the JavaScript it ships
   beside the module. The [Nim demo](./nim-demo) is its first user.
2. **The test host**, with [Testing](./testing).
3. **The rest of [Serving HTTP](#serving-http).** The HTTP host's component root, exporting `wasi:http/handler`, is
   built ([#2239](https://github.com/nanyx-lang/nanyx/issues/2239)).

# Open questions

- **Host configuration typing.** The `settings` of the manifest's `host` are checked against the settings the host
  declares in its manifest, with their defaults: a misspelt key is an error, and so is a value of another kind than its
  default. Whether to check them against a record type the host exports, as Nanyx types are checked, is open.
- **Host code in another language.** A browser host needs JavaScript beside the module. How does a host package carry
  it, and how does the build write it out? The playground host ships its runner in `packages/nanyx.playground.host/runner`,
  outside its `sources`, which the compiler built for the browser publishes with the page; a general mechanism is open.
- **Composing hosts.** Language-level, or a dedicated host? See [Composing hosts](#composing-hosts).
