---
title: "Files, HTTP and JSON"
description: "What is still open for the file system, HTTP and JSON packages"
order: 5
---

The file system, the HTTP client, the HTTP host and JSON are built, as shipped packages. What a program uses is in the
user docs: [filesystem](../stdlib/filesystem), [file](../stdlib/file), [dir](../stdlib/dir) and [path](../stdlib/path)
for `nanyx.fs`; [client](../stdlib/client), [http](../stdlib/http), [request](../stdlib/request) and
[response](../stdlib/response) for `nanyx.http.core`; [The HTTP host](../advanced/hosts#the-http-host) and the
[HTTP server cookbook](../cookbook/http-server) for `nanyx.http`; and [json](../stdlib/json),
[decoders](../stdlib/json-typed) and [encoders](../stdlib/encode) for `nanyx.json`. How the hosts implement them is on
[Hosts](./hosts#how-its-built) and [Foreign functions](./foreign-functions#whats-built). This page keeps what is still
open.

Every call blocks, for now. In a request handler, only that request's instance waits. When async comes, the calls
that wait require it ([Concurrency and crash boundaries](./concurrency-and-crash-boundaries)).

# The file system

- **A library that requires `FileSystem` under a host that doesn't provide it** passes the type checker and fails in
  the backend with an internal error, as one that requires `Console` does
  ([#1811](https://github.com/nanyx-lang/nanyx/issues/1811), which keeps
  [#1783](https://github.com/nanyx-lang/nanyx/issues/1783) open). It should be the error at the call that
  [Hosts](./hosts#hosts-provide-contexts) describes.
- **More than one `--allow-dir`.** The CLI's option parser takes one value per option, so `nanyx run` takes one extra
  directory; `WasmRunner` already takes any number.
- **A test host** that provides `FileSystem` in memory, with [Testing](./testing).

# HTTP

- **Binary bodies.** A body is a `string`, because nothing in the library turns an `Array(U8)` into a `string` yet.
  Binary bodies come with a conversion between bytes and text.
- **A `.json` helper.** `Response.json` would make `nanyx.http.core` depend on `nanyx.json`, and the HTTP host would
  want a constructor of that name as much as a client wants a reader. Both packages are built now, so this is open.
- **Route patterns.** There is no `#Path('/hello/{name}')` pattern yet, which [Serving HTTP](./hosts#serving-http)
  sketches; the cookbook splits the path and matches the parts with a list pattern.
- **More contexts for the HTTP host.** It provides `Http` only. `Env`, `Log` and `Clock`, which
  [Hosts](./hosts#serving-http) lists, aren't provided: `Clock` and `Log` don't exist yet, and `Env`'s members are
  written in the console host's root module, which another host can't share
  ([#1816](https://github.com/nanyx-lang/nanyx/issues/1816) is the start of that).
- **Serving more widely.** A server bound to other addresses than `localhost`, streamed bodies, and serving as a
  `wasi:http` component, which needs the backend to emit components. The client's runtime import, `nanyx:http`, is
  replaced by `wasi:http` then too, with no change to programs.

# JSON

- **Encoding and decoding any record automatically** ([#1789](https://github.com/nanyx-lang/nanyx/issues/1789)) is
  decided to wait for type reflection ([Type members and reflection](./type-members-and-reflection)), which lets any
  library walk a type's structure, not just JSON's. Deriving a JSON context in the compiler, as `Eq` and `ToString` are
  derived today, was rejected: it would be a compiler special case that a SQL or YAML library couldn't use.
- **Where the encoders live.** They are in a module of their own, `nanyx.json/encode`, because their names are the
  decoders' (`encode.string`, `json.string`). Attaching them to `Encoder` (`json.Encoder.string`) would keep one
  module, but a type's attached definitions can't be reached through an import's alias yet
  ([#1833](https://github.com/nanyx-lang/nanyx/issues/1833)).
- **The decoders' file.** They are in `json.typed.nyx`, part of the module `json`, because a partial module's file
  can't name a type from a file that sorts after it ([#1838](https://github.com/nanyx-lang/nanyx/issues/1838)).

# Open questions

- **Package names.** `nanyx.fs`, `nanyx.http.core`, `nanyx.http` and `nanyx.json` are working names, as the console
  packages' are ([Hosts](./hosts#open-questions)).
