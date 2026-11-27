---
title: "Components"
description: "How the compiler writes the core module of a WASI 0.3 component, and how the componentizer wraps it"
order: 17
---

The compiler can write a WebAssembly component that targets WASI 0.3, so that a program runs under Wasmtime, or any
runtime that implements WASI 0.3, and not only under `nanyx`'s own runtime. The design, and the stages still to come,
are on [Components](../work-in-progress/components) in Work in progress. `nanyx build` writes a component for a program
whose host has a component root, as the console and HTTP hosts do; `--core` writes a core module, which is all `nanyx run`,
`nanyx test`, the debugger and the profiler run.

A program can export functions of its own from its component, marked `@component`, for components in other languages
to call ([A program's own exports](#a-programs-own-exports)), and import theirs, marked `@import`
([A program's own imports](#a-programs-own-imports)).

A component is built in three steps:

1. The program is checked under its host's component root (`Package.asComponent`, see [Component roots](#component-roots)).
2. The backend writes a core module in wit-component's conventions (`WasmOptions.Component`).
3. The componentizer wraps it as a component of the root's world (`Nanyx.Runtime.Componentizer.componentize`).

# The core module

`WasmOptions.Component` changes three things about the module the backend writes (`ComponentAbi.fs`). It holds the
entry the module exports, a `ComponentEntry`, which the world says (`ComponentEntry.ofWorld`): `HttpHandler` for
`wasi:http/service`, and `CliRun` for any other.

- **The entry is exported under the world's name for it,** not run when the module is instantiated:
  - `CliRun` is `wasi:cli/run@0.3.0#run`, lifted async with a callback (`[async-lift]` and `[callback][async-lift]`),
    which gives `ok` with `task.return` once the program is done. While every task waits on the host, `run` returns to
    the host rather than block the instance ([Running with the callback ABI](#running-with-the-callback-abi)).
  - `HttpHandler` is `wasi:http/handler@0.3.0#handle`, lifted async with a callback ([The handler](#the-handler)).
- **`cabi_realloc` is exported,** for the host to allocate in the module's memory through. It is the program's own
  allocator: a new block of the size asked for, with the old block's bytes copied in and the old block freed. The
  allocator's blocks are only 4-byte aligned, so when the host asks for more (8, for a `list<u64>`, the most any WIT
  type needs), it takes the block from `allocAligned8`, which the
  [allocator](./memory-management) describes.
- **The runtime's own writes and exits go through `wasi:cli`,** not WASI preview 1. The runtime's helpers, `dbg` and a
  crash write with preview 1's `fd_write`, and exit with its `proc_exit`. A component's module defines both functions
  itself, with the same signatures, so every caller is unchanged:
  - `fd_write` writes each buffer to `wasi:cli/stdout` (fd 1) or `wasi:cli/stderr` (fd 2). Each write makes a
    `stream<u8>` (`[stream-new-0]write-via-stream`), gives its readable end to `write-via-stream`, and writes to its
    writable end until the host has taken every byte. Then it drops the writable end and waits on the future
    `write-via-stream` gave, so that the host has written the bytes before anything else is: stdout and stderr come out
    in the order they were written.
  - The write and the future's read are lowered async (`[async-lower]`), and when one returns BLOCKED the module waits
    for it at once, in a waitable set of its own (`[waitable-set-wait]`). Wasmtime turns the synchronous forms of
    `stream.write` and `future.read` off by default, and the async form followed by a wait runs with its defaults.
  - The waits write their event at addresses 16 to 31, below the string literals, where nothing else reads or writes.
  - `proc_exit` is `wasi:cli/exit`'s `exit-with-code`. A `wasi:http/service` component can't exit, so for
    `HttpHandler` it traps, and the host answers the request with an error.

  A foreign function of preview 1's `fd_write` or `proc_exit` is the same function, as it is in a module the runtime runs
  ([Foreign functions](./foreign-functions)), so `println` writes through `wasi:cli` too.

The module's imports are named as wit-component expects: the interface as the module and the function as the name, and
the canonical built-ins prefixed with what they are, such as `[stream-write-0]write-via-stream`, whose `0` is the
stream's place among the types in `write-via-stream`'s signature. The waitable-set built-ins come from `$root`.

A component's module has no probes, no memory checks and no `nanyx:debug` `dbg`, whose imports and exports no WASI world
has. A crash stops the program with a trap, as it does under `nanyx run`, having written why to stderr.

## The handler

`wasi:http/handler`'s `handle` takes a `request` and returns `result<response, error-code>`. The host reads the
response's body from a stream after `handle` returns, so a handler lifted synchronously, which returns only when its
core function does, can't write a body bigger than the stream's buffer: the host isn't reading it yet. So `handle` is
lifted async, with the callback ABI (`[async-lift]` and `[callback][async-lift]`), and gives its result with
`task.return` before it is done:

1. It raises backpressure (`[backpressure-inc]`), so that the host gives the instance no other request until it is
   done, and runs the program's start function, which answers the request through the component root's `run`. While
   every task waits on the host, it returns to the host, as `run` does
   ([Running with the callback ABI](#running-with-the-callback-abi)).
2. The root takes the request's handle from `argument`, and gives `result` the response it made, the writable ends of
   its body's `stream<u8>` and its trailers' future, and the address and length of the body, which `result` copies.
   `argument` and `result` are foreign functions of `nanyx:component` (`Hosts.componentModule`), which the module
   defines itself (`ComponentAbi.externs`) rather than imports; only a host's package, or a host library it depends on,
   may declare one (`NYX0276`).
3. Once the start function has finished, `handle` returns the response with `[task-return]handle`, then writes the
   body's copy to its stream, waiting while a write blocks, as `fd_write` does, and drops it; writes `ok(none)` to the
   trailers' future and drops it; frees the copy; lowers backpressure; and returns EXIT (`ComponentAbi.handleFinish`).
   The program has finished by then, and backpressure keeps other requests out, so nothing else could run while a
   write blocks.

The start function runs afresh for each request an instance is given: it sets the program's cached values up and tears
them down again, so nothing the program computes is kept from one request to the next.

## Running with the callback ABI

Under `CliRun` and `HttpHandler`, the start function is a state machine when it calls one (`CoreIrSplitPoints.stateMachines`,
given no start function to leave out), and its task, the main task, is one the scheduler runs as it runs the others
(`ComponentCallback`). A program whose start function never stops is run, gives its result and is done with EXIT at once.
A program's own export that can wait is an entry of the same kind ([Exports that wait](#exports-that-wait)). What follows
is `run`'s; `handle` does the same, with what it does before and after the start function ([The handler](#the-handler)).

0. `run` raises backpressure (`[backpressure-inc]`), and lowers it once it is done, so that the host starts no other
   task in the instance meanwhile, as `handle` does: one task at a time per instance, whichever entry it is, so one main
   task and one scheduler serve them all (settled 2026-11-26).

1. `run` calls the start function. When it stops, its frame goes to the main task, which goes on the queue, and the
   scheduler runs the tasks in turn, as `runOthers` does, resuming the start function's frame when the main task's turn
   comes.
2. When every task waits on the host, the scheduler makes the host's wait with `Callback.atTop` set. The component
   root's `componentWait` waits on its shared set through `wait`, a foreign function of `nanyx:component` that the module
   defines itself (`ComponentAbi.externs`): with `atTop` set, `wait` keeps the set and gives no event, rather than
   block in `[waitable-set-wait]`, and the scheduler returns WAIT, with the set, to the host.
3. The host calls the callback with an event when one comes. The callback keeps it and makes the host's wait the
   scheduler last made again; `wait` gives it the event, so `componentWait` keeps it for the task waiting on it, as
   when it waited itself. Then the scheduler runs again, from 2.
4. When the start function finishes, the scheduler tears the program's cached values down, gives `ok` with
   `[task-return]run`, and returns EXIT.

`TASK_CANCELLED` at the callback cancels the main task, and so every task, whose waits on a subtask cancel it
([below](#waiting-while-other-tasks-run)), while a stream's read or write goes on until the host has taken it; the
program carries on to its end, as a `race`'s loser does, and `run` gives its result. Only the scheduler sets `atTop`,
around its own wait: anywhere else, `wait` is `[waitable-set-wait]`. A wait on the host under code that runs the tasks to the end, a cached value's getter, would block
there too, which an `async func` may; but a cached value can't require a context, so its tasks can't wait on the host.
`ComponentCallbackTests.fs` and `ComponentCancellationTests.fs` run core modules on a fake host that calls `run` and its
callback as Wasmtime does.

# The componentizer

The wrapper around the core module is wit-component's, the library nearly every language uses for this. Nothing in
.NET writes components, so the compiler runs wit-component itself, compiled to WebAssembly:

- **`compiler/Nanyx.Componentizer`** is a Rust crate of a few lines on wit-component: given a core module, a WIT
  directory and a world's name, it embeds the world's types in the module (`embed_component_metadata`) and builds the
  component (`ComponentEncoder`), validating what it builds. It is a WASI preview 1 command, built for
  `wasm32-wasip1`, about 2 MB.
- **The built `.wasm` is committed** beside the crate, as `nanyx-componentizer.wasm`, and
  `Nanyx.Compiler.Runtime.fsproj` embeds it in the assembly, so building the compiler doesn't need Rust. After a change
  to the crate, `dotnet build -p:BuildComponentizer=true` rebuilds it with Cargo (which needs Rust and the
  `wasm32-wasip1` target) and `.github/scripts/componentizer-sources.sh --write` records the hash of the sources it was
  built from in `nanyx-componentizer.wasm.sources`. The Compiler workflow fails when that hash isn't the sources'.
- **`Componentizer.componentize`** runs it on the Wasmtime the runtime already embeds, with WASI preview 1: the core
  module in a temporary directory it may write to, and the WIT directory read-only. It gives the component, or
  wit-component's message saying why it can't build one, such as an import the world doesn't have.
- **The compiled componentizer is cached.** Compiling the 2 MB module takes a second or two, so the first run
  serializes it into the user's cache (`NANYX_CACHE`, or `.nanyx/cache` in their home directory), named by a hash of its
  bytes, and later runs read it from there. A cached file this Wasmtime can't read is compiled again.

The WIT directory is a WIT package, with the packages it uses under `deps/`, as wit-component reads it. The tests use
`wasi:cli@0.3.0` itself, with the WIT of WASI's v0.3.0 tag
(`compiler/Nanyx.Compiler.Wasm.Tests/Features/Components/wit`), and wrap modules as its world `command`.

# Component roots

A host's manifest names its component root and the world it is written against beside its root, and the world a
library component is built in, if it has one
(`component = (root = "cli.host.wasi", world = "nanyx:console/command", library = "nanyx:console/library")`), and
keeps the worlds' WIT in its `wit` directory. `HostPackages` reads it into `Host.Component`.

- **A component build swaps the root.** `nanyx build` takes the package as `Package.asComponent` gives it, whose host's
  `Module` is the component root (`Host.AsComponent`). Everything after that is a build as any other: the entry point is
  passed to that root's `run`, so it is checked against the contexts the component root provides, and the backend
  compiles the program with `WasmOptions.Component`. `Host.IsComponent` says whether a host is one a component build
  sees. The componentizer is given the host's `wit` directory and the world.
- **It provides what the core root does.** When the component root's own module is checked, in a component build,
  `Library.checkModule` imports the core root and compares the contexts the two `run`s provide; one only one of them
  provides is `NYX0275` (`Hosts.componentRootErrors`). So a program fits the one root when it fits the other, and an
  entry point that requires a context neither provides is the same error, `NYX0237`, naming it, in either build.
- **CI runs them.** The Compiler workflow builds each package in `examples/` as a component, runs it under
  `wasmtime run`, and checks that it prints what `nanyx run` does. A package under the HTTP host, `hello.server`, is
  served by `wasmtime serve` instead, and checked with requests in turn, which one instance answers. Its `/race`
  races a 20 ms sleep against a minute's, and must answer within 10 seconds, which it does only if `handle` returns to
  the host while both wait and the loser's sleep is cancelled.

## The console host's component root

`cli.host.wasi` provides `Console`, `Env`, `FileSystem`, `Http`, `Clock`, `Random` and `Async` on WASI 0.3 alone, in
the world `nanyx:console/command`, which the package's `wit/console.wit` declares: `wasi:cli/command@0.3.0` with
`wasi:http/client@0.3.0`. wit-component imports only the interfaces the core module uses, so a program that makes no
requests has no `wasi:http` import. It is Nanyx, on
`@extern`s named as wit-component expects, and it lowers the canonical ABI by hand with `nanyx/host.memory`:

- **Writes** to stdout and stderr go as the runtime's own do ([The core module](#the-core-module)): a `stream<u8>`
  given to the stream's `write-via-stream`, written until the host has taken every byte, then dropped, and its future
  waited on. The stdout and stderr imports are a record of functions (`Output`), so the one `writeTo` serves both.
  Imports the runtime's code also declares, such as `[stream-new-0]write-via-stream`, are imported twice, which
  WebAssembly and wit-component allow.
- **Standard input** is one stream, which `wasi:cli/stdin`'s `read-via-stream` opens the first time the program reads:
  its result is a tuple, written to memory the host is given the address of. Each read fills the buffer in the host's
  block from it. Taking lines and the rest of the input out of the buffer is `nanyx.wasi`'s `stdin`, shared with the core
  root, which fills the buffer with WASI preview 1's `fd_read`.
- **Arguments and environment variables** are `list<string>` and `list<tuple<string, string>>`, returned in memory: an
  address and a count, each string an address and a length. The host allocates them with `cabi_realloc`, which is the
  program's allocator, and the root frees each once it has copied it out.
- **The clocks and random numbers** are `wasi:clocks/system-clock`'s `now`, an `instant` of seconds and nanoseconds
  written to memory, `monotonic-clock`'s `now`, nanoseconds as a `u64`, and `wasi:random`'s `get-random-u64`. A sleep
  is `monotonic-clock`'s async `wait-for`, lowered with `[async-lower]`, whose subtask is waited on as a request's is
  ([below](#waiting-while-other-tasks-run)), so the program's other tasks run while it sleeps.
- **Exiting** is `wasi:cli/exit`'s `exit-with-code`, which takes 0 to 255; any other code exits with 1.
- **Files** are `wasi:filesystem`'s, in `cli.host.wasi.files.nyx`, a `@partial` fragment of the root. The preopens
  come from `get-directories` once, and are kept in the host's block; a path is found in one as the core root finds it
  in WASI preview 1's (`within`, in `nanyx.wasi`'s `files`, which both roots share, with `bytesAt`). A file is opened with
  `open-at`, read through `read-via-stream` from an offset, and written through `write-via-stream` or
  `append-via-stream`, which are `Output`s as stdout is, so `writeTo` writes a file too; its future's
  `result<_, error-code>` says whether the write went through. `stat`, `stat-at`, `create-directory-at`,
  `unlink-file-at` and `read-directory`, a `stream<directory-entry>` read 64 entries at a time, do the rest. Each
  descriptor is dropped (`[resource-drop]descriptor`) once the call is done with it.
  - These are `async func`s in WIT, lowered synchronously, which blocks, and runs with Wasmtime's default settings.
  - A result in memory is laid out as the canonical ABI lays it out: a `result<descriptor-stat, error-code>` has its
    case at 0 and its payload at 8, since `descriptor-stat` holds 64-bit fields; an `error-code` is its case, then
    `other`'s `option<string>` at 4, whose string the host allocated and the root frees.
  - An `error-code` becomes the `FsError` the core root gives for the same WASI error: `no-entry` is `#notFound`, and so
    on (`errorAt`).
- **HTTP requests** are `wasi:http/client`'s `send`, in the host library `nanyx.wasi` (`nanyx.wasi/http`), which the
  HTTP host's component root shares. A `Request` becomes a
  `wasi:http/types` `request`: its headers a `fields`, with a `Content-Length` for a body that doesn't name one, its body
  a `stream<u8>`, its trailers a future written `ok(none)`, its timeout `request-options`' connect and first-byte
  timeouts, and its URL split into scheme, authority and path with query (`urlParts`).
  - `send` is lowered async (`[async-lower]send`), so that the root can write the body while the host sends it; the
    root then writes the trailers and waits on the subtask until it has returned (`[subtask-drop]` after).
  - The response's status and headers (`copy-all`) are read, and its body from the stream `consume-body` gives; the
    future `consume-body` takes is written `ok` once the body is read, since a future's writable end can't be dropped
    unwritten.
  - An `error-code` becomes an `HttpError`: a timeout `#timeout`, a DNS or connection failure `#connectionFailed`, an
    invalid URI `#invalidUrl`, and anything else `#other`.

### Waiting while other tasks run

A request writes its body, waits for its response, and reads its body, while the program's other
[tasks](./async#tasks-and-scopes) run, and so does a sleep, and so does the HTTP host's component root as it reads the
request it serves. Its futures, the trailers it sends and the ones that say a body was read, are written so too.
`nanyx.wasi/waits` joins the subtask or the stream's end it waits on to one waitable set the instance shares, and waits
on the host (`nanyx/host.async`'s `waitOnHost`) until the event for it has come. When every task is waiting, the
scheduler makes the host's wait, `componentWait`, which waits on that set, through the compiler's `nanyx:component`
`wait` ([Running with the callback ABI](#running-with-the-callback-abi)), and keeps the event's code, in a list in the
host's block, for the task that waits on that waitable; that task takes it
when it looks again. The set's handle is at offset 40 of the host's block and the list's first cell at 44, which the
component roots leave free. A stream's write waits so in `writeAllAsync`, and a read or a future's write in
`settledAsync`. Every other wait blocks: each async-lowered call that returns BLOCKED, a write to stdout or a file,
a read of stdin or a file, is waited on at once, in a waitable set of its own, as the runtime's own writes are. Those
are `Console`'s and `FileSystem`'s, whose functions don't require `Async`, so they can't stop the task that calls them.
`examples/sleepers` is a package whose two tasks wake in the order they do only if a sleep lets the other run, and CI
runs it as a component under Wasmtime beside `nanyx run`. `ComponentWaitTests.fs` runs a request whose body's write
blocks on a fake host, and checks that another task's sleep starts and ends meanwhile.

**A cancelled task cancels its subtask.** The scheduler wakes a task that waits on the host when it is cancelled
([Cancellation](./async#tasks-and-scopes)), and one waiting for a subtask, a sleep or a request, takes it out of the
shared set, which `[subtask-cancel]` requires, and cancels it, unless the event that it returned is kept already: the
canonical ABI traps on cancelling a subtask whose return was delivered. The synchronous `[subtask-cancel]` waits until
the host has settled the subtask, and gives the state it ended in, which `awaitCallAsync` gives back: a sleep then
returns, and a request gives `#cancelled` without reading an answer, which the host never wrote. So a `race`'s loser
stops what it waits on when it loses. `examples/racers` races a short sleep against a minute's, and says whether the race
was over at once, which it is only if the loser's sleep was cancelled; CI runs it under Wasmtime too, and
`ComponentCancellationTests.fs` runs the core module on a fake host that traps where the canonical ABI does. A stream's
read or write, or a future's write, in a cancelled task still goes on until the host has taken it, or dropped its end:
a request's body and its trailers are written in full before the request is cancelled, and a response's body is read
to its end.

## The HTTP host's component root

`http.host.wasi` provides `Http`, `Clock`, `Random` and `Async`, as the console host's root does, in the world
`wasi:http/service@0.3.0`. The package's `wit/http.wit` is a package of its own with nothing in it, which the
componentizer needs as the root of the directory, and `wit/deps` holds WASI 0.3's WIT. Its `run` answers the request
`handle` was given ([The handler](#the-handler)) through `nanyx.wasi/http.service`:

- **The request** is read from the `wasi:http/types` `request` that `argument` gives. Its method is a `method` variant,
  whose case is a `Method`; one `Request` has no case for, `CONNECT`, `TRACE` or another, is answered with a 400 without
  calling the handler. Its URL is put together from its scheme, `http` unless it is `HTTPS`, its authority, and its path
  with its query. Its headers are copied out of its `fields` (`headersOf`, shared with the client), and its body is
  read from the stream `consume-body` gives (`bodyFrom`, shared too), whose future is then written `ok`.
- **The response** is a `response` made with `response.new` from a `fields` with its headers (`fieldsOf`), a new
  `stream<u8>` and a new future for its trailers, whose readable ends go to the host with it, and its status set with
  `set-status-code`. The future `response.new` gives, which says how sending the response went, is dropped. A response
  whose headers can't be sent is a 500 with no headers and no body.

# A program's own exports

A program exports functions of its own from its component, for a component in another language to call, by marking
them `@component` ([#2241](https://github.com/nanyx-lang/nanyx/issues/2241)). The component's WIT is generated from
their Nanyx types: Nanyx is the source of truth, and the WIT is written for other languages to generate their bindings
from.

```nanyx fragment
module shapes

@component
export def areaOf: float, float -> float = { width, height -> width * height }
```

## Which defs

- **`@component` comes before an exported def,** with no argument (`Attributes.applyAttributes`); before a def its
  module doesn't export, it is `NYX0008`. The def is exported in the WIT interface its module names: the name its file
  declares with `module`, or else its file's name, or `main`.
- **A program exports the defs of every module it imports,** directly or not, and its own. Each module's checked form
  keeps its own and those its imports carry (`TypedModule.ComponentExports`), each by the name it is inlined under in
  the importer (`ModuleLinking.componentExportsForImport`), and inlining keeps them though the program never calls
  them (`ModuleLinking.reachedItems`). A module the program doesn't import exports nothing.
- **A package with no `@entry` is a library component** if its modules mark defs `@component` ([#2326](https://github.com/nanyx-lang/nanyx/issues/2326)).
  `nanyx build` builds it from a root of its own making (`Package.libraryResult`), `__component.nyx` in the package's
  directory, which is no file on disk: it imports each module of the package with such a def
  (`Package.componentModules`) and does nothing else, so the component exports them all. Its core module exports no
  `run` (`ComponentEntry.Library`), and its world includes the host's world for libraries, which imports what the
  host's other world does and exports nothing (`HostComponent.Library`): the console host's is `nanyx:console/library`,
  `wasi:cli/imports@0.3.0` with `wasi:http/client@0.3.0`. A host that names none can't build one, and says so.
- **Its type must cross a component's boundary** (`ComponentInterface.errors`, run as each module is checked). A
  function, a context, a type variable, a record or tag union with no name, which WIT must name, a union of types
  other than tags, a recursive type, which WIT can't write, and a def that isn't a function at all are `NYX0277`. What
  the compiler can't take across yet is `NYX0301`, naming it. An export that requires contexts its type doesn't write,
  only inferred, is `NYX0301` too, asking for them to be written.
- **An export that requires contexts runs under its host's** ([#2326](https://github.com/nanyx-lang/nanyx/issues/2326)),
  as the entry point does. Each host's root may export `provide`, which calls a function under the contexts the root
  provides and gives back what it returns; the console host's two roots do, and their `run` is `provide`'s result
  ignored. For an export whose written type requires contexts, `Attributes.applyAttributes` imports the host, as for an
  `@entry`, and makes up a def that calls it through `provide`, at the attribute: `@component export def later: [Clock]
  int -> int = { delay -> ... }` gets `def __component_later = { delay -> __host.provide({ -> later(delay) }) }`
  (`Hosts.providedExportName`). The component exports that def in its place (`ComponentExport.Definition`), with the
  export's type less its contexts (`ComponentInterface.withoutContexts`), and its parameters' names, which are the
  export's. It writes no type, so it requires what `provide` does, `Async` and `HostRuntime`, which no other module
  may require: `Hosts.runtimeErrors` lets it, and its call of `provide`, as it does an entry point's call of `run`.
  `HostRuntime` and `Async` have no members, so the def's function takes only the export's parameters. A context the
  root doesn't provide is `NYX0237` at the attribute (`Hosts.exportMismatch`), and so is every such export under a host
  whose root has no `provide`, as the HTTP host's hasn't (`Hosts.unprovidedExport`); the def is then left out.

## The WIT

`ComponentInterface` writes each type as the WIT type it is, and each name in kebab case (`areaOf` is `area-of`, the
module `geo.shapes` the interface `geo-shapes`), escaping a WIT keyword with `%`:

| Nanyx | WIT |
|---|---|
| `bool` | `bool` |
| `I8`, `I16`, `I32`, `int` | `s8`, `s16`, `s32`, `s64` |
| `U8`, `U16`, `U32`, `U64` | `u8`, `u16`, `u32`, `u64` |
| `F32`, `float` | `f32`, `f64` |
| `string` | `string` |
| `list(a)` | `list<a>` |
| a tuple, `(int, string)` | `tuple<s64, string>` |
| a named record, `type Point = (x: int, y: int)` | `record point { x: s64, y: s64 }` |
| a named tag union, `type Shape = #circle(float) \| #empty` | `variant shape { circle(f64), empty }` |
| a function's `()` | no parameters, or no result |

- **A record or tag union is named by the type that names it,** and defined in the interface of each function that
  uses it, once, after the types it is made of. A generic one is named with its arguments, so `Pair(int)` is
  `pair-s64`, and `Pair(list(Point))` is `pair-list-point`. A named type that stands for anything else, such as
  `type Id = int`, is written as what it stands for, and so is a nominal type.
- **Every tag union is a `variant`,** with a case for each tag, as written, and a payload of the tag's type, a tuple
  for a tag of several. `#some(a) | #none` is a variant too, not an `option`, and `#ok(a) | #error(e)` not a `result`.
- **A record's fields are in the order they are written,** and a variant's cases in the order its tags are.
- **A parameter is named as the def's lambda names it.**

The package's WIT package is named from its manifest (`ComponentInterface.packageName`): `acme.shapes` at 0.1.0 is
`acme:shapes@0.1.0`, the first segment the namespace, and a name of one segment is both, `greeter:greeter@0.1.0`. For a
program that exports anything, `nanyx build` writes its WIT to `out/wit` beside the component
(`ComponentInterface.write`): `component.wit`, with an interface for each module and a world, `component`, that includes
the host's world and exports them; and under `deps`, the host's own package (`deps/host`) and the packages its world is
made of. The componentizer is given that directory and world, rather than the host's.

## The core module

The backend gets the functions and the names they are exported under (`WasmOptions.ComponentExports`), each the
interface's qualified name, `#` and the function's, `acme:shapes/shapes@0.1.0#area-of`. The function of each export's
def is a root for inlining and for what the module keeps (`CoreIrInline.inlineCalls`, `CoreIrReach.reachedFrom`), and
`ComponentExports` writes a function that the module exports under that name, lifted synchronously unless it can wait
([below](#exports-that-wait)):

`ComponentValues` writes the code that lifts a value of each WIT type into a Nanyx value of the type the backend
lowered its Nanyx type to, and lowers one back, following the canonical ABI's layout in memory and its flattening to
core values ([CanonicalABI.md](https://github.com/WebAssembly/component-model/blob/main/design/mvp/CanonicalABI.md)).
A WIT record's fields are a Nanyx record's by name, a tuple's by position, and a variant's cases a union's tags by
name, so the two needn't be in the same order.

- **The arguments** come as core values: numbers as they are, since Nanyx holds each in the core type the canonical
  ABI passes it as, a sized integer narrower than 32 bits extended from its width; a string or a list as an address
  and a length; a record or a tuple as its items' values in turn; and a variant as its discriminant, then the values
  of its payload in places its cases share, each the type that holds what each case puts there, an `f64` in an `i64`
  read back with `f64.reinterpret_i64`. Arguments that come to more than 16 core values come in a block the host
  allocated, laid out as a tuple of them, which is freed once they are lifted.
- **A string's bytes and a list's elements** are in blocks the host allocated through `cabi_realloc`, which the module
  then owns. Each is copied into a Nanyx string (`stringFromMemory`) or list, made from its last element, and the
  block freed with what its elements point to.
- **An argument the def's function doesn't take over** (`CoreIrCounting`) is dropped after the call.
- **A result** that comes to at most one core value is returned as it: a number, or a variant whose tags have no
  payloads, as its discriminant. Any other is written into a new block, its strings and lists into new blocks of their
  own, and the block's address returned. The Nanyx result is dropped once it is written. The module exports
  `cabi_post_` and the export's name too, which the host calls once it has copied the result, and which frees the
  block and every block it points to.

An export runs on whatever the instance has set up: a top-level value is built the first time something reads it,
and the start function tears it down again at the end of `run`, so an export called before `run`, after it, or with no
`run` at all works as one called during it.

## Exports that wait

An export whose type requires `Async`, which a def that waits must write, is an `async func` in the WIT
([#2915](https://github.com/nanyx-lang/nanyx/issues/2915)). Whether it can wait is decided when its module is checked,
from the contexts its def requires (`ComponentExport.Waits`, `ComponentInterface.requiresAsync`), so the WIT and the
backend agree; one that requires only contexts that can't wait, such as `Console`, stays a `func`.

```nanyx fragment
@component
export def later: [Async, Clock] int -> int = { ms ->
  sleep(ms * 1ms)
  ms
}
```

`ComponentExports` makes it an entry lifted with the callback ABI, `[async-lift]` and the export's name, with a
`[callback]` ([Running with the callback ABI](#running-with-the-callback-abi)):

- It raises backpressure, lifts its arguments as a synchronous export does, and keeps those its def doesn't take over
  in globals of its own, since it may return to the host before they can be dropped. Then it calls its def's function.
- While every task waits on the host, it and its callback return WAIT, as `run` does.
- Once the function has finished, it gives the result with `[task-return]` and the function's name, imported from
  `[export]` and the interface's: as the core values the result is passed as, or past 16 of them the address of a block,
  lowered into a new block first and freed, with what it points to, once `task.return` has copied it. Then it drops the
  result and the arguments it kept, lowers backpressure, and returns EXIT. It has no `cabi_post_`.

A synchronous export couldn't do this: the component model doesn't let a task whose type isn't `async` wait before it
returns. `AsyncExportTests.fs` calls exports that sleep on a fake host, which gives them their events through their
callbacks, and `compiler/ComponentInterop` has a Rust component await one under Wasmtime ([below](#testing-with-other-languages)).

# A program's own imports

A program calls a function another component exports by declaring it, marked `@import`, with its type and no value
([#2241](https://github.com/nanyx-lang/nanyx/issues/2241)):

```nanyx fragment
@import("acme:geometry/shapes@1.0.0", "area", ["shape"])
def area: Shape -> float
```

- **The attribute names the WIT interface in full,** its package, its name and its version, the function, whose
  name is the def's in kebab case if it is left out, and the function's parameters, which are `p0`, `p1` and on if
  they are left out (`Attributes.applyAttributes`). The def's value becomes a call of `__componentImport` with them
  (`ComponentImportSyntax`), which the type checker takes to be of the def's written type, as it does a foreign
  function's. Its type must cross a component's boundary, as an export's does, and it must name each of the
  function's parameters if it names any, `NYX0013` (`ComponentInterface.errors`).
- **A program imports what it calls.** An import is inlined into the modules that use it as any def is, so
  `ComponentInterface.imports` finds those the program reaches. The world `nanyx build` writes imports each interface
  they name, and the WIT of each interface's package is written from the program's declarations of it under
  `out/wit/deps` (`ComponentInterface.importedPackages`): the component model checks types by their structure, by
  the names of fields and cases, not by the names of types, so no WIT of the other component's is read.
- **A call of one is a call of a foreign function** (`CallImport`), of the interface and the function, whose parameters
  and result are the core types the Nanyx values are held in (`ComponentInterface.externOf`). The module defines that
  function itself (`ComponentImports`), and imports the interface's function under another name, with the canonical
  ABI's signature: the arguments as their core values, or the address of a block of them past 16, then the address of
  a block for a result of more than one core value; and a result of one as it.
- **The function lowers its arguments,** which it borrows, as an export lowers its result: into a new block, their
  strings and lists into blocks of their own, passed as their core values (`Generator.FlatFromMemory`), a variant's
  payload widened into the places its cases share, or by the block's address. It frees all of it after the call. It
  lifts the result as an export lifts its arguments, from the core value or from the block it allocated for it, whose
  strings and lists the callee allocated through `cabi_realloc`, and frees those blocks.
- **Outside a component, nothing provides an import.** In a core module, as `nanyx run` and `nanyx test` run and
  `nanyx build --core` and `nanyx wasm` write, a call of one that the program reaches is `NYX0278`, naming it, at its def.

## Names at the boundary

A component's WIT is what another language's bindings are generated from, and the component model checks more than a
value's shape: an interface's types, a record's fields, a variant's cases and a function's parameters are matched by
name. So the other component's WIT must be the WIT `nanyx build` writes, as it is for the Rust components below. An
export's parameters are named by its def's lambda. An import's have no names in its Nanyx type, so `@import` names
them, as the other component's WIT does, and they are `p0`, `p1` and on if it doesn't.

## Testing with other languages

`compiler/ComponentInterop` holds a Nanyx library component (`shapes`) and two Rust components: a WASI 0.3 command (`app`)
that calls the Nanyx component's exports, one of which, `announce`, requires `Console` and prints under the console
host's, and another, `later`, sleeps, and a component (`units`) whose exports the Nanyx component imports. `app`'s `run`
is an `async func`, which awaits `later`: a synchronous `run`, as a WASI 0.2 command's is, may not wait for it. `run.sh` builds the
three, checks that the Rust components' WIT is what `nanyx build` wrote, composes them with `wac plug`, and runs the
result under `wasmtime run`, which must print `expected.txt`. The Compiler workflow runs it on Linux, with Wasmtime,
`wac` and Rust's `wasm32-wasip2` target installed at pinned versions.
