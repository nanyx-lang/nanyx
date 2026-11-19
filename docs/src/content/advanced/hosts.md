---
title: "Hosts"
description: "What runs a program's entry point, which contexts it provides, and how to write one with foreign functions"
order: 11
---

A **host** is what runs a Nanyx program. It takes the program's entry point, loads the contexts the program may use, such as `Console`, and runs it. Every application names its host, and the compiler checks that the entry point fits it.

A host is an ordinary Nanyx package. The compiler ships one, the console host `nanyx.console`, for command-line programs, and anyone can write another. Only a host can reach the world outside the program, through the foreign functions its embedder provides, so a library can do nothing the host's contexts don't allow.

# Choosing a host

A package names its host in its manifest's `host` field. A host the compiler ships is named at the compiler's version:

```nanyx fragment
(
  package = (name = "acme.tool", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.console", version = "0.28.0")
)
```

Any other host is found by its directory, relative to the manifest, as a dependency is, and has no version: `host = (package = "acme.server", path = "../server")`. `nanyx new` writes the console host into a new package's manifest.

A package whose manifest names no host is a **library**. It can't be run, and an `@entry` in it is an error (`NYX0031`). See [Packages and projects](../tour/packages#entrypoints).

[`nanyx run`, `nanyx build` and `nanyx check`](../standard-tools/command-line#running-and-checking-code) take `--host` to run a package under another host for one command: a host the compiler ships, `--host nanyx.console`, or the directory of one, `--host ../server`.

A single `.nyx` file runs under the console host unless a `::host` directive at its top names another, in the same words a manifest would use (see [Directives](../getting-started/files-and-projects#directives)):

```nanyx fragment
::host acme.server ../server
```

## The entry point fits the host

The entry point is passed to the host's `run` function, so it must have the type `run` takes. Under the console host that is a function with no parameters, which may require `Async`, `Console`, `Env`, `FileSystem`, `Http` and `Random`. An entry point of another shape is an error at its `@entry` that names the host and both types:

```text
main.nyx:3:1: error NYX0237: The entry point 'main' doesn't fit the host 'nanyx.console/console.host'
  expected: [Async, Console, Env, FileSystem, Http, Random] () -> a
  found:    int -> int
```

A script's statements are its entry point, and run under its host in the same way.

# The console host

The console host, `nanyx.console`, runs command-line programs. It provides six contexts:

- [`Console`](../stdlib/console) reads standard input and writes standard output and standard error.
- [`Env`](../stdlib/env) gives the program's command-line arguments (`args`) and environment variables (`variable`), and ends it with an exit code (`exit`).
- [`FileSystem`](../stdlib/filesystem) reads and writes files, through the functions of the shipped package `nanyx.fs` ([`file`](../stdlib/file), [`dir`](../stdlib/dir) and [`path`](../stdlib/path)). `nanyx run` lets a program reach the current directory and, with [`--allow-dir`](../standard-tools/command-line#nanyx-run), one other; a path outside them is `#error(#permissionDenied)`.
- [`Http`](../stdlib/client) makes HTTP requests, through the functions of the shipped package `nanyx.http.core` ([`http`](../stdlib/http)). Each call waits for its response.
- [`Async`](../stdlib/async) lets a function wait. The calls of `nanyx.fs` and `nanyx.http.core` wait, so they require it, and so does every function that calls them.
- [`Random`](../stdlib/random) gives random numbers, drawn from the operating system's random source, through the functions of `nanyx/random`. `random.seeded` gives the same numbers on every run instead.

The host has a _prelude_: a module whose exports every module of the program sees without importing them. The console host's brings in `Async`, `Console`, `Env`, `FileSystem`, `Http`, `Random`, `print`, `println`, `eprint` and `eprintln`, so a program uses them with no import:

```nanyx
match args()
  | [name, ..._] -> println("Hello, {name}!")
  | [] -> exit(2)
```

`nanyx run greet.nyx Ada` prints `Hello, Ada!`; with no arguments, the program ends with the exit code 2. Arguments for the program go after the file or package.

`Console` and `Env` are declared in a separate package, `nanyx.console.core`, which the compiler ships too. A library has no host, so it sees no host's prelude: it lists `nanyx.console.core` in its dependencies, at the compiler's version, and imports what it uses:

```nanyx fragment
(
  package = (name = "acme.report", version = "0.1.0")
  dependencies = [(package = "nanyx.console.core", version = "0.28.0")]
)
```

```nanyx fragment
export module greetings

import nanyx.console.core/console as (Console, println)

export def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

An application that depends on the library calls `greet` from its entry point, which the console host gives a `Console`.

# The HTTP host

The HTTP host, `nanyx.http`, serves a program whose entry point is a request handler, a `Request -> Response`
function. It provides `Http`, for requests the handler makes itself, with `Async`, so that the handler can wait on
them, and `Random`, and neither `Console` nor `FileSystem`. While one handler waits, the host answers other requests. Each
request is answered by a fresh instance of the program, so nothing is shared between requests, and a handler that
stops the program ends only its own request, which is answered with a 500.

```nanyx fragment
@entry
def main: Request -> Response = { request ->
  match Request.path(request)
    | "/" -> Response.text(200, "Welcome!")
    | _ -> Response.text(404, "Not Found")
}
```

A package names it as its host with the port to serve on, `host = (package = "nanyx.http", version = "0.28.0",
settings = (port = 8080))`, and `nanyx run` serves it there, on 3000 when no port is given. A host declares the
settings it takes, each with its default, so a setting it doesn't take is an error at the setting. See the
[HTTP server cookbook](../cookbook/http-server).

# Where `dbg` prints

[`dbg`](../stdlib/console#dbg) needs no context, so it works under every host, but where what it prints goes is the
host's to decide. A debug build that `nanyx run` runs gives each `dbg` it reaches to the runtime first, with where it is
written and the value's text:

- **The console host** writes it to standard error, `[main.nyx:12] 84`, in order with what the program writes there
  itself.
- **The HTTP host** writes it to the server's log, with the request it came from, `dbg (GET /hello): [src/main.nyx:5]
  "Ada"`, and never into the response.
- **A host of your own** gets the console host's: the line on standard error. So does a module written by `nanyx build`
  or `nanyx wasm` and run by another WebAssembly runtime, which writes it there itself.

A release build has no `dbg` to print.

# Hosts and contexts

A host provides the contexts a program may require, and only those. A function that requires a context the host doesn't provide can't be called from the entry point, so a host that provides no `FileSystem` guarantees that neither the program nor any library it uses touches the file system. A library says what it needs in its types, `[Console]`, and runs under every host that provides it.

# Writing a host

A host is a package whose manifest says it is one, in its `package`'s `host` field: `root` names the module that exports `run`, and `prelude`, which may be left out, the module whose exports every module of a program it runs sees.

```nanyx fragment
(
  package = (
    name = "acme.shout"
    version = "0.1.0"
    sources = ["src/**/*.nyx"]
    host = (root = "shout.host", prelude = "shout.prelude")
  )
)
```

Naming a package as a host when its manifest doesn't declare it one is an error. The host here provides one context, `Log`, which writes a line in capitals. The context is declared in a module of its own:

```nanyx fragment
--| What a program under the shouting host can do.
export module shout.log

--| Writes a line, loudly.
export context Log = (line: string -> ())
```

The prelude re-exports it, so a program calls `line` without importing anything:

```nanyx fragment
export module shout.prelude

export shout.log (Log)
```

The root module exports `run`, which takes the entry point, loads the contexts the host provides with `use`, and calls it. `run`'s parameter type is the entry shape the host accepts: here, a function with no parameters that may require `Log`. What the entry point returns is ignored.

```nanyx fragment
export module shout.host

import (
  nanyx/host.memory
  nanyx/host.runtime
  nanyx/text
  shout.log as (Log)
)

@extern("wasi_snapshot_preview1", "fd_write")
def fdWrite: [HostRuntime] I32, I32, I32, I32 -> I32

def writeLine: [HostRuntime] string -> () = { text ->
  def loud = string.uppercase(text) ++ "!\n"
  def iovec = allocate(12)
  writeI32(iovec, addressOf(loud))
  writeI32(iovec + 4, byteLength(loud))
  ignore(fdWrite(1, iovec, 1, iovec + 8))
  keepAlive(loud)
  free(iovec)
}

export def run: [HostRuntime] ([Log] () -> a) -> () = { entry ->
  use Log(line = { text -> writeLine(text) })
  ignore(entry())
}
```

An application that names this host, `host = (package = "acme.shout", path = "../shout")`, gets `Log` and nothing else:

```nanyx fragment
module main

@entry
def main = {
  line("hello")
}
```

It prints `HELLO!`. A call to `println` in it is an unknown function, since this host provides no `Console`.

## `HostRuntime`

`run` and the functions it calls require [`HostRuntime`](../stdlib/host-runtime), a context with no members that marks the host. The toolchain provides it only to the host's root module, the one its manifest's `root` names; anywhere else, a function that requires it, or calls one that does, is an error (`NYX0238`). Foreign functions and host memory require it, so only the host can call them, and a library can only reach the outside world through the contexts the host chose to provide.

## Foreign functions

A **foreign function** is a function the WebAssembly module imports from its embedder: under `nanyx run`, a WASI call such as `fd_write`. It is declared with the [`@extern`](../tour/attributes#extern) attribute, which names the import's module and name, and a def with a type and no value:

```nanyx fragment
@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()
```

- **Its type** takes and returns only WebAssembly's value types, written as the sized numbers `I32`, `I64`, `F32` and `F64`, or `()` for no result. Strings, records and lists don't cross the boundary: a host passes their addresses and lengths, in [host memory](#host-memory). Any other type, a value after the type, or arguments other than two strings is an error (`NYX0241`).
- **It requires `HostRuntime`**, and a type without it is an error (`NYX0242`). Any module may declare a foreign function, but only a host can call one.
- **Each import is declared once.** Two foreign functions that name the same import with different types are an error (`NYX0243`).
- **The embedder provides it.** `nanyx run` provides WASI preview 1's functions. An import it doesn't provide fails when the module is loaded, with an error that names the import.

A foreign function can be called, but naming one as a value, rather than calling it, can't be compiled yet.

## Host memory

A foreign function deals in addresses in the module's linear memory: WASI's `fd_write` reads the bytes it writes from a buffer, and `args_get` writes the arguments into one. [`nanyx/host.memory`](../stdlib/host-memory) gives a host that access. Every function in it requires `HostRuntime`:

- `allocate` and `free` a block of bytes;
- `readByte`, `readI32` and `readI64`, and `writeByte`, `writeI32` and `writeI64`, at an address;
- `addressOf` and `byteLength`, where a string's bytes are and how many there are, and `stringAt`, a new string copied from an address and a length;
- `keepAlive`, which keeps a string alive until it is called, for one whose address is written into memory a foreign function reads, as the iovec above is;
- `hostState`, the address of 4096 bytes set aside for the host, all zero at the start, for what it keeps from one call to the next.

These are the only unchecked operations a host performs. A read or a write outside the module's memory traps, but one inside it where the host has no block reads or overwrites the program's own data.

# Packages the compiler ships

The compiler ships the packages every program needs beside itself, in a `packages` directory next to the `nanyx` executable, so a program builds and runs with nothing downloaded:

- `nanyx.console`, the console host;
- `nanyx.console.core`, its interface package, which declares `Console` and `Env`;
- `nanyx.fs`, the interface package of the file system, which declares `FileSystem`, `Path` and the functions on them;
- `nanyx.http.core`, the interface package of HTTP, which declares `Http`, `Request`, `Response` and the functions on them;
- `nanyx.http`, the HTTP host;
- `nanyx.json`, for reading and writing [JSON](../stdlib/json);
- `nanyx.regex`, for [regular expressions](../stdlib/regex).

A manifest or a directive names a package the compiler ships at the compiler's version exactly; any other version is an error that names the one shipped (`NYX0029`). In a `::host` or `::dependency` directive it needs no version at all: `::dependency nanyx.console.core`. The standard library, `nanyx/...`, is built into the compiler and is never listed as a dependency.
