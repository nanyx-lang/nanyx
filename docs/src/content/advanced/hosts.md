---
title: "Hosts"
description: "What runs a program's entry point, which contexts it provides, and how to write one with foreign functions"
order: 11
---

A **host** is what runs a Nanyx program. It takes the program's entry point, loads the contexts the program may use, such as `Console`, and runs it. Every application names its host, and the compiler checks that the entry point fits it.

A host is an ordinary Nanyx package. The compiler ships one, the console host `nanyx.cli.host`, for command-line programs, and anyone can write another. Only a host can reach the world outside the program, through the foreign functions its embedder provides, so a library can do nothing the host's contexts don't allow.

# Choosing a host

A package names its host in its manifest's `host` field. A host the compiler ships is named at the compiler's version:

```nanyx fragment
(
  package = (name = "acme.tool", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.cli.host", version = "0.49.1")
)
```

Any other host is found by its directory, relative to the manifest, as a dependency is, and has no version: `host = (package = "acme.server", path = "../server")`. `nanyx new` writes the console host into a new package's manifest.

A package whose manifest names no host is a **library**. It can't be run, and an `@entry` in it is an error (`NYX0031`). See [Packages and projects](../tour/packages#entrypoints).

[`nanyx run`, `nanyx build` and `nanyx check`](../standard-tools/command-line#running-and-checking-code) take `--host` to run a package under another host for one command: a host the compiler ships, `--host nanyx.cli.host`, or the directory of one, `--host ../server`.

A single `.nyx` file runs under the console host unless a `::host` directive at its top names another, in the same words a manifest would use (see [Directives](../getting-started/files-and-projects#directives)):

```nanyx fragment
::host acme.server ../server
```

## The entry point fits the host

The entry point is passed to the host's `run` function, so it must have the type `run` takes. Under the console host that is a function with no parameters, which may require `Async`, `Clock`, `Console`, `Env`, `FileSystem`, `Http` and `Random`. An entry point of another shape is an error at its `@entry` that names the host and both types:

```text
main.nyx:3:1: error NYX0237: The entry point 'main' doesn't fit the host 'nanyx.cli.host/cli.host'
  expected: [Async, Clock, Console, Env, FileSystem, Http, Random] () -> a
  found:    int -> int
```

A script's statements are its entry point, and run under its host in the same way.

# The console host

The console host, `nanyx.cli.host`, runs command-line programs. It provides seven contexts:

- [`Console`](../stdlib/console) reads standard input and writes standard output and standard error.
- [`Env`](../stdlib/env) gives the program's command-line arguments (`args`) and environment variables (`variable`), and ends it with an exit code (`exit`).
- [`FileSystem`](../stdlib/filesystem) reads and writes files, through the functions of the shipped package `nanyx.fs` ([`file`](../stdlib/file), [`dir`](../stdlib/dir) and [`path`](../stdlib/path)). `nanyx run` lets a program reach the current directory and, with [`--allow-dir`](../standard-tools/command-line#nanyx-run), one other; a path outside them is `#permissionDenied`.
- [`Http`](../stdlib/client) makes HTTP requests, through the functions of the shipped package `nanyx.http` ([`http`](../stdlib/http)). Each call waits for its response.
- [`Async`](../stdlib/async) lets a function wait. The calls of `nanyx.fs` and `nanyx.http` wait, so they require it, and so does every function that calls them.
- [`Random`](../stdlib/random) gives random numbers, drawn from the operating system's random source, through the functions of `nanyx/random`. `random.seeded` gives the same numbers on every run instead.
- [`Clock`](../stdlib/clock) gives the time of day, `now()`, an `Instant`, and `ticks()`, a `Time(int)` that never goes backwards, for timing.

The host has a _prelude_: a module whose exports every module of the program sees without importing them. The console host's brings in `Async`, `Clock`, `Console`, `Env`, `FileSystem`, `Http`, `Random`, `print`, `println`, `eprint` and `eprintln`, so a program uses them with no import:

```nanyx
match args()
  | [name, ..._] -> println("Hello, {name}!")
  | [] -> println("Hello, whoever you are!")
```

`nanyx run greet.nyx Ada` prints `Hello, Ada!`; with no arguments, it prints `Hello, whoever you are!`. Arguments for the program go after the file or package.

`Console` and `Env` are declared in a separate package, `nanyx.cli`, which the compiler ships too. A library has no host, so it sees no host's prelude: it lists `nanyx.cli` in its dependencies, at the compiler's version, and imports what it uses:

```nanyx fragment
(
  package = (name = "acme.report", version = "0.1.0")
  dependencies = [(package = "nanyx.cli", version = "0.49.1")]
)
```

```nanyx fragment
export module greetings

import nanyx.cli/console as (Console, println)

export def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

An application that depends on the library calls `greet` from its entry point, which the console host gives a `Console`.

## Built as a component

`nanyx build` writes a program under the console host as a [WebAssembly component](../deep-dives/components), which
`wasmtime run` (46 or later), or any runtime that implements WASI 0.3, runs as it is: `wasmtime run out/greet.wasm Ada`.
`nanyx run` runs the program as it always has, and `nanyx build --core` writes a core module, as before.

A component is built from the host's _component root_, `cli.host.wasi`, which provides everything the console host
does, on WASI 0.3. Its world is `nanyx:console/command`, which is `wasi:cli/command@0.3.0` with
`wasi:http/client@0.3.0`, and a component imports only what the program uses:

- A program that makes no HTTP requests is a `wasi:cli/command` component, which any WASI 0.3 runtime runs.
- One that does imports `wasi:http/client` too: `wasmtime run -S http out/greet.wasm` provides it.
- A component reaches only the directories the runtime that runs it opens for it: `wasmtime run --dir . out/greet.wasm`
  opens the current directory, where a relative path is found.

# The HTTP host

The HTTP host, `nanyx.http.host`, serves a program whose entry point is a request handler, a `Request -> Response`
function. It provides `Http`, for requests the handler makes itself, with `Async`, so that the handler can wait on
them, `Random` and `Clock`, and neither `Console` nor `FileSystem`. While one handler waits, the host answers other requests. Each
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

A package names it as its host with the port to serve on, `host = (package = "nanyx.http.host", version = "0.49.1",
settings = (port = 8080))`, and `nanyx run` serves it there, on 3000 when no port is given. A host declares the
settings it takes, each with its default, so a setting it doesn't take is an error at the setting. See the
[HTTP server cookbook](../cookbook/http-server).

## Served as a component

`nanyx build` writes a program under the HTTP host as a [WebAssembly component](../deep-dives/components) of the world
`wasi:http/service@0.3.0`, which `wasmtime serve` (Wasmtime 51 or later), or any runtime that implements the world, serves as it
is: `wasmtime serve -S cli out/app.wasm` serves it on port 8080, with `-S cli` for the stdout and stderr the program
writes `dbg` and its crashes to. `nanyx run` serves the program as it always has, and `nanyx build --core` writes a core
module, as before.

A component is built from the host's component root, `http.host.wasi`, which provides `Http`, `Async`, `Random` and
`Clock` on WASI 0.3, as the core root does. Neither root provides `Env`: the world has no environment to read.

- **The runtime may reuse an instance.** `wasmtime serve` gives an instance of the component up to 128 requests, one
  after another, and the program runs afresh for each, so nothing the program computes is kept from one request to the
  next. The instance's memory is, though, and a request that crashes ends its instance, which the runtime answers with
  a 500. `wasmtime serve --max-instance-reuse-count 1` gives each request an instance of its own, as `nanyx run` does.
- **An instance answers one request at a time.** The runtime gives it no second request until it has written the
  first's response, and starts other instances for requests that come in meanwhile.
- **A request whose method `Request` has no case for,** such as `CONNECT` or `TRACE`, is answered with a 400, without
  calling the handler.

# The playground host

The playground host, `nanyx.playground.host`, runs a console program in a web page, as the [playground](../standard-tools/playground)
does. It stands in for the console host: it provides the same `Console`, from `nanyx.cli`, whose output goes
to the page, `Random`, drawn from a generator the page seeds, `Clock`, the browser's time, and `Async`, so that a
program can start [tasks](./async) and sleep, so a program that needs only those compiles under it unchanged. It
provides nothing else, no `Env`, `FileSystem` or `Http`, so a program that needs one of them is an error naming the
host:

```
error NYX0205: 'file.read' requires the context 'FileSystem', which the host 'nanyx.playground.host/playground.host' does not provide
```

The compiler built for the browser runs a script under it unless the script's `::host` names another; elsewhere a
script runs under it with `::host nanyx.playground.host`. The package ships the JavaScript that runs such a program in a web
worker and passes what it prints, and what its `dbg`s print, to the page. Reading a line waits for a line typed in the
page, and gives `#endOfInput` once the input has ended.

# The test host

The test host, `nanyx.test.host`, runs one test of a program's: a test build runs under it in place of the program's own
host, and its entry point runs the test at the index the host gives it ([Test builds](../deep-dives/test-builds)). It
provides `Console`, whose output it gives the runtime and whose input is empty, and `Random`, seeded with the runtime's
seed as [`random.seeded`](../stdlib/random) seeds it, and nothing else, so a test that requires any other context loads
it with `use` (`NYX0312`). A test marked [`@test(#io)`](../tour/attributes#test) has the console host's real `Console`,
`Env` and `FileSystem` instead, written in the host library `nanyx.wasi`, and `Async`.

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
    name = "acme.shout.host"
    version = "0.1.0"
    sources = ["src/**/*.nyx"]
    host = (root = "shout.host", prelude = "shout.prelude")
  )
)
```

A host's name ends in `.host`, and only a host's does: the library a host runs programs against, which declares the
contexts it provides, takes the short name, and the host is that name with `.host` after it, as `nanyx.cli` and
`nanyx.cli.host` are. A program names its host once, in its manifest, while a library names the short name wherever it
imports the library, so the short name goes to the library. A package that declares a `host` under another name, or is
named `.host` and declares none, is an error at its name ([`NYX0053`](../standard-tools/errors#nyx0053)).

Naming a package as a host when its manifest doesn't declare it one is an error.

A host may also have a component root, which `nanyx build` builds a [WASI 0.3 component](../deep-dives/components)
from: `component = (root = "shout.host.wasi", world = "wasi:cli/command@0.3.0")`, beside `root`. It exports `run` as the
root does, written against the WIT of the world it names, which the package keeps in its `wit` directory, with the
packages that WIT uses under `wit/deps`. It provides the contexts the root does: one more or fewer is an error at its
`run` (`NYX0275`). A host whose programs may be library components, packages with `@component` defs and no `@entry`,
also names the world they are built in, which imports what its other world does and exports nothing:
`library = "nanyx:console/library"`, beside `world`. Without one, building a library component under the host is an
error.

A host's roots may also export `provide`, which takes a function of the shape `run` does, calls it under the contexts
the host provides, and gives back what it returns: `export def provide: [HostRuntime] ([Log] () -> a) -> a`. A
component's export that requires contexts, an [`@component`](../tour/attributes#component) def such as
`[Clock] int -> int`, is called through it, so it runs under the host's contexts as the entry point does. A context
`provide` doesn't provide is an error at the export (`NYX0237`), and under a host whose roots have no `provide`, as the
HTTP host's haven't, so is every export that requires contexts. The console host's roots export one, and their `run`
calls it. The host here provides one context, `Log`, which writes a line in capitals. The context is declared in a module of its own:

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

`run` and the functions it calls require [`HostRuntime`](../stdlib/host-runtime), a context with no members that marks the host. The toolchain provides it only to the modules of the host's own package (its root, its component root, and the modules they share) and of the [host libraries](#host-libraries) it depends on; anywhere else, including the other packages the host depends on, a function that requires it, or calls one that does, is an error (`NYX0238`). Foreign functions and host memory require it, so only the host can call them, and a library can only reach the outside world through the contexts the host chose to provide.

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

## Waiting on the host

A call that waits, such as a request, shouldn't hold up the program's other [tasks](./async). A host's code that waits
starts what it waits for, then looks whether it has finished, and between looks calls
[`nanyx/host.async`](../stdlib/host-async)'s `waitOnHost`. That stops the task, and the scheduler runs the others. Once
every task is waiting, the scheduler calls the host's wait, the function `waitOnHost` was given, which blocks until
something the host started may have finished, or until a timeout. Then each task that waited on the host looks again.
The console host's requests work this way, on the runtime's `nanyx:http` and its `nanyx:async` wait:

```nanyx fragment
@extern("nanyx:async", "wait")
def runtimeWait: [HostRuntime] I32 -> ()

def answerOf: [Async, HostRuntime] I32, I32 -> I32 = { handle, kind ->
  def length = httpPoll(handle, kind)

  if length >= 0 then length else
    waitOnHost(runtimeWait, -1)
    answerOf(handle, kind)
}
```

The hosts' component roots wait the same way, on one waitable set the instance shares
([Components](../deep-dives/components#waiting-while-other-tasks-run)).

The second argument is how long the task can wait before it must look again, in milliseconds, or -1 for as long as it
takes: a sleep passes what is left of it. The scheduler calls the host's wait with the shortest any task passed. A host
whose waits block, as WASI preview 1's file functions do, needs none of this: the call holds the instance until it is
done, and the other tasks run after it.

## Host libraries

Hosts share what they are written on through _host libraries_: packages whose manifest says `hostLibrary = true` in its `package`. A host library's modules may require `HostRuntime`, as a host's own do, when the build host's package depends on it, or when the library is checked or packed on its own, so two hosts call the same foreign functions without each declaring them:

```nanyx fragment
(
  package = (name = "acme.platform", version = "0.1.0", sources = ["src/**/*.nyx"], hostLibrary = true)
)
```

A program that depends on a host library can import it, but can't call what requires `HostRuntime` there, since the program's own code may not require it. The compiler ships one, `nanyx.wasi`, which the console host and the HTTP host are written on.

# Packages the compiler ships

The compiler ships the packages every program needs beside itself, in a `packages` directory next to the `nanyx` executable, so a program builds and runs with nothing downloaded:

- `nanyx.cli.host`, the console host;
- `nanyx.cli`, its interface package, which declares `Console` and `Env`;
- `nanyx.fs`, the interface package of the file system, which declares `FileSystem`, `Path` and the functions on them;
- `nanyx.http`, the interface package of HTTP, which declares `Http`, `Request`, `Response` and the functions on them;
- `nanyx.http.host`, the HTTP host;
- `nanyx.wasi`, the [host library](#host-libraries) the console and HTTP hosts are written on: the console's `Console`, `Env` and `FileSystem` on WASI preview 1, WASI's random source and clocks, the runtime's `nanyx:http`, and the component model's canonical ABI and WASI 0.3 interfaces their component roots use;
- `nanyx.playground.host`, the playground host, which runs a console program in a web page;
- `nanyx.test.host`, the test host, which runs one test of a program's in a test build.

A package the compiler ships is a package from the [package index](../tour/packages#package-dependencies) like any other: a manifest names it at a version, and the lockfile locks it with its SHA-256. The compiler seeds the version it ships into the package cache, so that version needs no download, and a build with no network finds it. A host, and the host library hosts are written on, build with the compiler they ship with only. In a `::host` or `::install` directive a package the compiler ships needs no version at all, `::install nanyx.cli`, which is the version it ships. The standard library, `nanyx/...`, is built into the compiler and is never listed as a dependency.
