---
title: "Files, HTTP and JSON"
description: "The libraries a useful program needs first: the file system, HTTP requests and serving, and JSON"
order: 5
---

Reading and writing files, making and serving HTTP requests, and reading and writing JSON are the minimum for most
useful programs. This page is their design, decided by the maintainer on 2026-11-17. None of it is built yet.

# What they share

- **Each capability is a context, in a small interface package of its own,** as `Console` and `Env` are in
  `nanyx.console.core` ([Libraries for a host](./hosts#libraries-for-a-host)). Libraries depend only on the
  interface, and hosts provide the implementation. That's capability security: a host that doesn't provide
  `FileSystem` guarantees that no code in the program, libraries included, touches the file system.

  | Context | Package | Provided by |
  |---|---|---|
  | `FileSystem` | `nanyx.fs` | the console host, and a test host (in memory) |
  | `Http` | `nanyx.http.core` | the console host, and the HTTP host |

  JSON needs no context: `nanyx.json` is pure.
- **They're shipped packages,** with the compiler, as `nanyx.console.core` is, and named in a manifest's
  `dependencies` or a script's `::dependency`. The standard library stays what every program needs.
- **Every call blocks.** There's no async, as decided for hosts that can block: in a request handler, only that
  request's instance waits.
- **Failures are tag unions,** matched like any value, never traps: `#some(string) | #error(FsError)`.
- **Text is UTF-8.** Bytes are `Array(U8)`.

# The file system: `nanyx.fs`

```nanyx fragment
::dependency nanyx.fs

def config = file.read(path.of("config.json"))

match config
  | #some(text) -> println(text)
  | #error(#notFound) -> println("No config file")
  | #error(e) -> println("Couldn't read the config: {e}")
```

- **Paths are a type,** `Path`, made with `path.of`, with `join`, `parent`, `fileName` and `extension`. That catches a
  string passed where a path was meant, and normalises separators.
- **Functions:** `file.read`, `readBytes`, `write`, `append`, `exists` and `delete`, and `dir.list` and `dir.create`.
  `FsError` is `#notFound | #permissionDenied | #isDirectory | #notDirectory | #alreadyExists | #other(string)`.
- **Lazily, line by line:** `file.lines(path)` is a `[FileSystem] seq(string)`
  ([Lazy sequences](./lazy-sequences)), so a large file is never read whole.
- **Scoped by default.** The console host implements it with WASI's file calls. `nanyx run` gives a program the
  current directory and nothing outside it unless `--allow-dir` adds more, and anything outside is
  `#permissionDenied`.

# HTTP: `nanyx.http.core`, then the HTTP host

**The client** comes first:

```nanyx fragment
::dependency nanyx.http.core

match http.get("https://example.com/api/users")
  | #some(response) -> println(response.status)
  | #error(e) -> println("Request failed: {e}")
```

- `Request` and `Response` are records: method, URL, headers (a `map(string, string)`) and body (bytes, with `.text`
  and `.json` helpers). `http.get`, `post` and `send` return `#some(Response) | #error(HttpError)`.
- **How it reaches the network.** WASI preview 1, which the backend emits today, has no networking. Until the backend
  emits components and can use the standard `wasi:http`, the `nanyx` runtime supplies a small import of its own,
  backed by .NET's `HttpClient`, and the console host implements `Http` on it. Programs see only `Http`, so the swap
  changes nothing for them.

**Serving** is the HTTP host already designed on the [Hosts](./hosts#serving-http) page: a host package, `nanyx.http`,
whose entry is a `Request -> Response` handler. It runs each request in a fresh instance, and provides `Http` but not
`Console` or `FileSystem`. `nanyx run` serves it locally first; serving it as a `wasi:http` component comes later.

# JSON: `nanyx.json`

Two layers.

1. **A `Json` value, with parsing and printing:**
   `#null | #bool(bool) | #number(float) | #string(string) | #array(list(Json)) | #object(map(string, Json))`, with
   `json.parse`, `json.print` and `json.printPretty`.
2. **Typed encoding and decoding with combinators,** as the [JSON cookbook](../cookbook/json-parsing) shows:
   `json.record(name = json.field("name", json.string), ...)`, with errors that name where they failed
   (`users[2].age`). Numbers are `float`, and `json.int` checks that one is whole.

**Encoding and decoding any record automatically** is decided to wait for type reflection
([Type members and reflection](./type-members-and-reflection)), which lets any library walk a type's structure, not
just JSON's. Deriving a JSON context in the compiler, as `Eq` and `ToString` are derived today, was rejected: it would
be a compiler special case that a SQL or YAML library couldn't use.

# Plan

| Issue | What | After |
|---|---|---|
| [#1783](https://github.com/nanyx-lang/nanyx/issues/1783) | `nanyx.fs`: `FileSystem`, `Path`, the functions, in the console host | |
| [#1784](https://github.com/nanyx-lang/nanyx/issues/1784) | `file.lines` as a lazy `seq` | #1783 |
| [#1785](https://github.com/nanyx-lang/nanyx/issues/1785) | `nanyx.http.core` and the client, through a runtime import | |
| [#1786](https://github.com/nanyx-lang/nanyx/issues/1786) | The HTTP host, served locally by `nanyx run` | #1785 |
| [#1787](https://github.com/nanyx-lang/nanyx/issues/1787) | `nanyx.json`: the `Json` value, parsing and printing | |
| [#1788](https://github.com/nanyx-lang/nanyx/issues/1788) | `nanyx.json`: encoders and decoders | #1787 |
| [#1789](https://github.com/nanyx-lang/nanyx/issues/1789) | Automatic encoding and decoding, on type reflection (Planning) | #1788, reflection |

# Open questions

- **A dropped `file.lines`.** Does dropping a sequence that wasn't read to its end close the file (a drop hook on a
  value), or does it stay open until the program ends? Settled in #1784.
- **The order of an object's keys.** Does `Json`'s `#object` keep the order they were written in, or is it a plain
  map? Settled in #1787.
- **Package names.** `nanyx.fs`, `nanyx.http.core`, `nanyx.http` and `nanyx.json` are working names, as the console
  packages' are.
