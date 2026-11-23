---
title: "Files, HTTP and JSON"
description: "What is still open for the file system, HTTP and JSON packages"
order: 5
---

The file system, the HTTP client and the HTTP host are built, as shipped packages, and JSON as `nanyx.json`, published
to the package index from [nanyx-lang/json](https://github.com/nanyx-lang/json). What a program uses is in the
user docs: [filesystem](../stdlib/filesystem), [file](../stdlib/file), [dir](../stdlib/dir) and [path](../stdlib/path)
for `nanyx.fs`; [client](../stdlib/client), [http](../stdlib/http), [request](../stdlib/request) and
[response](../stdlib/response) for `nanyx.http.core`; [The HTTP host](../advanced/hosts#the-http-host) and the
[HTTP server cookbook](../cookbook/http-server) for `nanyx.http`; and [json](../stdlib/json),
[decoders](../stdlib/json-typed) and [encoders](../stdlib/encode) for `nanyx.json`. How the hosts implement them is on
[Hosts](../deep-dives/hosts#how-its-built) and [Foreign functions](../deep-dives/foreign-functions#whats-built). This
page keeps what is still open.

# The file system

- **More than one `--allow-dir`.** The CLI's option parser takes one value per option, so `nanyx run` takes one extra
  directory; `WasmRunner` takes any number.
- **A test host** that provides `FileSystem` in memory, with [Testing](./testing).

# HTTP

- **Binary bodies.** A body is a `string`. The library now converts between bytes and text, with
  [`string.toUtf8` and `string.fromUtf8`](../stdlib/text), so a body could be an `Array(U8)` and a program could read
  it as text when it is UTF-8; `Request` and `Response` don't take one yet.
- **A `.json` helper.** `Response.json` would make `nanyx.http.core` depend on `nanyx.json`, and the HTTP host would
  want a constructor of that name as much as a client wants a reader. Whether to add it is open.
- **What the HTTP host still needs**, route patterns, `Log` and `Env`, streamed bodies, serving beyond `localhost`
  and serving as a `wasi:http` component, is on [Serving HTTP](./hosts#serving-http).
# JSON

- **Encoding and decoding any record automatically** ([#1789](https://github.com/nanyx-lang/nanyx/issues/1789)) is
  decided to wait for type reflection ([Type members and reflection](./type-members-and-reflection)), which lets any
  library walk a type's structure, not just JSON's. Deriving a JSON context in the compiler, as `Eq` and `ToString` are
  derived, was rejected: it would be a compiler special case that a SQL or YAML library couldn't use.
- **Where the encoders live.** They are in a module of their own, `nanyx.json/encode`, because their names are the
  decoders' (`encode.string`, `json.string`). Attaching them to `Encoder` (`json.Encoder.string`) would keep one
  module, since a type's attached definitions are reached through an import's alias
  ([#1833](https://github.com/nanyx-lang/nanyx/issues/1833)); whether to move them is open.
- **The decoders' file.** They are in `json.typed.nyx`, part of the module `json`. A partial module's file can name a
  type from a file that sorts after it ([#1838](https://github.com/nanyx-lang/nanyx/issues/1838)), so they could be
  laid out in other files; whether to is open.

# Open questions

- **Package names.** `nanyx.fs`, `nanyx.http.core`, `nanyx.http` and `nanyx.json` are working names, as the console
  packages' are ([Hosts](./hosts#open-questions)).
