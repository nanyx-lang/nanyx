---
title: "Files, HTTP and JSON"
description: "The libraries a useful program needs first: the file system, HTTP requests and serving, and JSON"
order: 5
---

Reading and writing files, making and serving HTTP requests, and reading and writing JSON are the minimum for most
useful programs. This page is their design, decided by the maintainer on 2026-11-17. The file system is built, but for
`file.lines` and the check described under [Still open](#still-open-for-the-file-system), and so is the first layer of
`nanyx.json`, the `Json` value with parsing and printing ([#1787](https://github.com/nanyx-lang/nanyx/issues/1787)).
The HTTP client is built ([#1785](https://github.com/nanyx-lang/nanyx/issues/1785)); serving HTTP and JSON's encoders
and decoders are not built yet.

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

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

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
  ([Lazy sequences](./lazy-sequences)), so a large file is never read whole. Not built yet
  ([#1784](https://github.com/nanyx-lang/nanyx/issues/1784)).
- **Scoped by default.** The console host implements it with WASI's file calls. `nanyx run` gives a program the
  current directory and nothing outside it unless `--allow-dir` adds more, and anything outside is
  `#permissionDenied`.

## How the file system is built

- **The package.** `packages/nanyx.fs` is a shipped package with four public modules: `filesystem` declares
  `FileSystem` and `FsError`; `path` declares `Path`, a `protected string` that `path.of` normalises (`/` between the
  parts, no empty or `.` parts, a `..` taken with the part before it, `.` for the empty path, and `/` or a drive such as
  `C:/` as a root); `file` and `dir` are the functions, each a call of a `FileSystem` member. There is no lowercase
  alias for a type a library declares, so `path.of` and `file.read` are module-qualified calls: a program imports each
  module under its own name, `import nanyx.fs/path as path`. Its reference is on the
  [filesystem](../stdlib/filesystem), [file](../stdlib/file), [dir](../stdlib/dir) and [path](../stdlib/path) pages.
- **What was settled while building it.** `file.write`, `append` and `delete` and `dir.create` give `#ok` or
  `#error(FsError)`; `file.exists` gives a `bool`, false for a path the program may not reach. `dir.list` gives the
  paths of what a directory holds, its path joined with each name, sorted by name; the member under it,
  `listDirectory`, gives the names in any order, so another host has less to do. `file.delete` deletes only files: a
  directory is `#isDirectory` on every system.
- **The members.** `FileSystem`'s members are named for what they do on their own (`readFile`, `writeFile`,
  `pathExists`, `listDirectory`, ...), since the console host's prelude re-exports the context and so makes them
  ambient names in every module of a program. It re-exports `Path` and `FsError` too: a program module sees `run`'s
  parameter type, `[Console, Env, FileSystem] () -> a`, so it must be able to name the members' types.
- **The console host.** `nanyx.console` depends on `nanyx.fs`, and its `run` `use`s a `FileSystem` beside `Console`
  and `Env`, written in `console.host.nyx` on WASI's `fd_prestat_get` and `fd_prestat_dir_name`, `path_open`,
  `fd_read`, `fd_write`, `fd_close`, `fd_filestat_get`, `fd_readdir`, `path_filestat_get`, `path_unlink_file` and
  `path_create_directory`. A path is found in the runtime's preopened directories: a relative one in the preopen
  named `.`, an absolute one in the preopen whose name starts it (compared without case when it starts with a drive),
  and one in none is `#permissionDenied`, as is a relative path that climbs out of `.`, which WASI refuses
  (`ENOTCAPABLE`). WASI's error numbers map to `FsError`. As for `Env`, the inliner leaves out the members a program
  doesn't call, so a program that reads no files imports none of these functions.
- **The runtime.** `WasmRunner` (`Nanyx.Compiler.Runtime/Runner.fs`) takes the directories a program may reach, the
  current directory first: it preopens that one as `.` and by its absolute path, and each other by its absolute path,
  written with `/`. `nanyx run` passes the shell's directory and the one `--allow-dir` names (`Program.RunIn`); a
  directory that isn't there is a usage error. Output a test redirects is written by WASI to files of its own, then
  copied to the caller's streams, so that `fd_write` stays WASI's and writes to files too.

## Still open for the file system

- **A library that requires `FileSystem` under a host that doesn't provide it** passes the type checker and fails in
  the backend with an internal error, as one that requires `Console` does
  ([#1811](https://github.com/nanyx-lang/nanyx/issues/1811)). It should be the error at the call that
  [Hosts](./hosts#hosts-provide-contexts) describes.
- **More than one `--allow-dir`.** The CLI's option parser takes one value per option, so `nanyx run` takes one extra
  directory; `WasmRunner` already takes any number.
- **A test host** that provides `FileSystem` in memory, with [Testing](./testing).

# HTTP: `nanyx.http.core`, then the HTTP host

**The client** comes first, and is built ([#1785](https://github.com/nanyx-lang/nanyx/issues/1785)); its reference is
on the [client](../stdlib/client), [http](../stdlib/http), [request](../stdlib/request) and
[response](../stdlib/response) pages:

```nanyx fragment
::dependency nanyx.http.core

import nanyx.http.core/http as http

match http.get("https://example.com/api/users")
  | #some(response) -> println(response.status)
  | #error(_) -> println("Request failed")
```

- `Request` and `Response` are records: method, URL, headers (a list of names and values) and body. `http.get`, `post`
  and `send` return `#some(Response) | #error(HttpError)`, and `HttpError` is
  `#timeout | #connectionFailed(string) | #invalidUrl | #other(string)`.
- **How it reaches the network.** WASI preview 1, which the backend emits today, has no networking. Until the backend
  emits components and can use the standard `wasi:http`, the `nanyx` runtime supplies a small import of its own,
  `nanyx:http`, backed by .NET's `HttpClient`, and the console host implements `Http` on it (see
  [Foreign functions](./foreign-functions#whats-built)). Programs see only `Http`, so the swap changes nothing for
  them.

## How the client is built

- **The package.** `packages/nanyx.http.core` has four public modules, laid out as `nanyx.fs` is: `request` declares
  `Method` (`#get | #post | #put | #delete | #patch | #head | #options`) and `Request`, with
  `Request.of(method, url)`; `response` declares `Response`, with `Response.text(status, body)`, which a handler of the
  HTTP host will make its answers with, and `Response.header`, which finds a header whatever its case; `client`
  declares `Http` and `HttpError`; and `http` has `get`, `post` and `send`, imported as `http`. The context's one
  member is `sendRequest`, named for what it does on its own, since the console host's prelude makes it an ambient
  name.
- **Settled while building it.**
  - **The body is text,** a `string`, not bytes: nothing in the library turns an `Array(U8)` into a `string`, so a
    byte body would leave every program unable to read a response, and the [Hosts](./hosts#serving-http) page's
    handler takes its body as text too. So there is no `.text` helper: `response.body` is the text. Binary bodies come
    with a conversion between bytes and text in the library.
  - **A request has a `timeout`,** in milliseconds, which `Request.of` sets to 30 seconds: `#timeout` needs one, and
    a request is where it belongs, as `HttpClient` has one per call.
  - **Headers are a list** of names and values, `list((string, string))`, not the `map(string, string)` the design
    named: a header may come more than once (`Set-Cookie`), and its order can matter. A map also hit a compiler bug,
    [#1841](https://github.com/nanyx-lang/nanyx/issues/1841): any map the console host builds broke every program with a
    `def map` of its own. Names are in lower case in a response, as HTTP/2 writes them, and `Response.header`
    finds the first of a name whatever its case; a request's are sent as written.
  - **No `.json` helper yet.** `Response.json` would make `nanyx.http.core` depend on `nanyx.json`, and the HTTP host
    will want a constructor of that name as much as a client wants a reader; it waits for the HTTP host and the JSON
    encoders ([#1788](https://github.com/nanyx-lang/nanyx/issues/1788)).
- **The console host** depends on `nanyx.http.core`; its `run` `use`s an `Http` after `FileSystem`, so its parameter is
  `[Console, Env, FileSystem, Http] () -> a`, and its prelude re-exports `Http`, `HttpError`, `Request`, `Method` and
  `Response`. It writes a request as `nanyx:http` reads it, calls `send`, allocates the answer's length, calls
  `receive`, and reads the response back. As with the file system, a program that doesn't make requests imports
  nothing for it.
- **Calls block.** `send` waits on `HttpClient` in the runtime's thread, so the program stops until the answer comes:
  in the HTTP host, only the instance that sends it.

**Serving** is the HTTP host already designed on the [Hosts](./hosts#serving-http) page: a host package, `nanyx.http`,
whose entry is a `Request -> Response` handler. It runs each request in a fresh instance, and provides `Http` but not
`Console` or `FileSystem`. `nanyx run` serves it locally first; serving it as a `wasi:http` component comes later.

# JSON: `nanyx.json`

Two layers.

1. **A `Json` value, with parsing and printing** (built, [#1787](https://github.com/nanyx-lang/nanyx/issues/1787)):
   `#null | #bool(bool) | #number(float) | #string(string) | #array(list(Json)) | #object(map(string, Json))`, with
   `json.parse`, `json.print` and `json.printPretty(value, indent = 2)`. Its reference is the [json](../stdlib/json)
   page.
2. **Typed encoding and decoding with combinators,** as the [JSON cookbook](../cookbook/json-parsing) shows:
   `json.record(name = json.field("name", json.string), ...)`, with errors that name where they failed
   (`users[2].age`). Numbers are `float`, and `json.int` checks that one is whole.

How the first layer is built, in `packages/nanyx.json/src/json.nyx`, in Nanyx:

- **An object keeps its keys in the order they were written.** `#object` holds a `map(string, Json)`, and a map keeps
  its keys in the order they were first added, so `parse` adds them as the text writes them and `print` writes them
  back in that order. A key written twice holds its last value, in the place it was first written. Two objects with
  the same members in another order are still `==`, as maps are.
- **Errors say where.** `JsonError` is `(position: int, line: int, column: int, message: string)`: the byte, and its
  line and column (in characters), each from 1. The messages follow the compiler's [house style](./error-messages#house-style):
  `Expected ',' or ']' after an element of an array, found the end of the text`.
- **Text is read a byte at a time,** through a `StringSlice` without copying it, and a string's run between escapes is
  copied once. `\uXXXX` escapes are decoded, with surrogate pairs joined and a lone surrogate an error. Each function
  calls itself in tail position along an array or an object, so a document is read and written in time in proportion
  to its size (a test reads and writes one of over 100,000 values); only nesting takes stack.
- **Numbers.** `parse` checks JSON's grammar for a number (no leading `0`, no `+`, digits after a `.`) and reads it with
  `float.fromString`. `print` writes a whole number below 10^15 without a fraction (`42`), any other as
  `float.toString` does, and NaN and infinity, which JSON can't write, as `null`.
- **Imported under a name.** `import nanyx.json/json as json`: a plain import would bring in a `print` that clashes with
  the console's. Two compiler bugs show up in use: a derived `==` or `ToString` of a `Json` or `JsonError` needs the
  types imported by name too ([#1796](https://github.com/nanyx-lang/nanyx/issues/1796)), and a `Json` literal whose
  lists hold different tags must name the inner values first
  ([#1798](https://github.com/nanyx-lang/nanyx/issues/1798)).

**Encoding and decoding any record automatically** is decided to wait for type reflection
([Type members and reflection](./type-members-and-reflection)), which lets any library walk a type's structure, not
just JSON's. Deriving a JSON context in the compiler, as `Eq` and `ToString` are derived today, was rejected: it would
be a compiler special case that a SQL or YAML library couldn't use.

# Plan

| Issue | What | After |
|---|---|---|
| [#1783](https://github.com/nanyx-lang/nanyx/issues/1783) | `nanyx.fs`: `FileSystem`, `Path`, the functions, in the console host: built, but for [#1811](https://github.com/nanyx-lang/nanyx/issues/1811) | |
| [#1784](https://github.com/nanyx-lang/nanyx/issues/1784) | `file.lines` as a lazy `seq` | #1783 |
| [#1785](https://github.com/nanyx-lang/nanyx/issues/1785) | `nanyx.http.core` and the client, through a runtime import (done) | |
| [#1786](https://github.com/nanyx-lang/nanyx/issues/1786) | The HTTP host, served locally by `nanyx run` | #1785 |
| [#1787](https://github.com/nanyx-lang/nanyx/issues/1787) | `nanyx.json`: the `Json` value, parsing and printing (done) | |
| [#1788](https://github.com/nanyx-lang/nanyx/issues/1788) | `nanyx.json`: encoders and decoders | #1787 |
| [#1789](https://github.com/nanyx-lang/nanyx/issues/1789) | Automatic encoding and decoding, on type reflection (Planning) | #1788, reflection |

# Open questions

- **A dropped `file.lines`.** Does dropping a sequence that wasn't read to its end close the file (a drop hook on a
  value), or does it stay open until the program ends? Settled in #1784.
- **Package names.** `nanyx.fs`, `nanyx.http.core`, `nanyx.http` and `nanyx.json` are working names, as the console
  packages' are.
