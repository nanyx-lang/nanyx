---
title: "Components"
description: "How the compiler writes the core module of a WASI 0.3 component, and how the componentizer wraps it"
order: 17
---

The compiler can write a WebAssembly component that targets WASI 0.3, so that a program runs under Wasmtime, or any
runtime that implements WASI 0.3, and not only under `nanyx`'s own runtime. The design, and the stages still to come,
are on [Components](../work-in-progress/components) in Work in progress. `nanyx build` writes a component for a program
whose host has a component root, as the console host does; `--core` writes a core module, which is all `nanyx run`,
`nanyx test`, the debugger and the profiler run.

A component is built in three steps:

1. The program is checked under its host's component root (`Package.asComponent`, see [Component roots](#component-roots)).
2. The backend writes a core module in wit-component's conventions (`WasmOptions.Component`).
3. The componentizer wraps it as a component of the root's world (`Nanyx.Runtime.Componentizer.componentize`).

# The core module

`WasmOptions.Component` changes three things about the module the backend writes (`ComponentAbi.fs`):

- **The entry is exported as `wasi:cli/run@0.3.0#run`,** not run when the module is instantiated. It calls the start
  function, if the program has one, and returns 0, which is `ok`. `run` is an `async func` in WIT, lifted synchronously:
  the instance blocks in each call it makes, as it does under `nanyx run`.
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
  - `proc_exit` is `wasi:cli/exit`'s `exit-with-code`.

  A foreign function of preview 1's `fd_write` or `proc_exit` is the same function, as it is in a module the runtime runs
  ([Foreign functions](./foreign-functions)), so `println` writes through `wasi:cli` too.

The module's imports are named as wit-component expects: the interface as the module and the function as the name, and
the canonical built-ins prefixed with what they are, such as `[stream-write-0]write-via-stream`, whose `0` is the
stream's place among the types in `write-via-stream`'s signature. The waitable-set built-ins come from `$root`.

A component's module has no probes, no memory checks and no `nanyx:debug` `dbg`, whose imports and exports no WASI world
has. A crash stops the program with a trap, as it does under `nanyx run`, having written why to stderr.

# The componentizer

The wrapper around the core module is wit-component's, the library nearly every language uses for this. Nothing in
.NET writes components, so the compiler runs wit-component itself, compiled to WebAssembly:

- **`compiler/Nanyx.Componentizer`** is a Rust crate of a few lines on wit-component: given a core module, a WIT
  directory and a world's name, it embeds the world's types in the module (`embed_component_metadata`) and builds the
  component (`ComponentEncoder`), validating what it builds. It is a WASI preview 1 command, built for
  `wasm32-wasip1`, about 2 MB.
- **`Nanyx.Compiler.Runtime.fsproj` builds it with Cargo** before it compiles, and embeds the `.wasm` in the assembly.
  So building the compiler needs Rust and the `wasm32-wasip1` target; using it doesn't.
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

A host's manifest names its component root and the world it is written against beside its root
(`component = (root = "console.host.wasi", world = "nanyx:console/command")`), and keeps the world's WIT in its `wit`
directory. `HostPackages` reads it into `Host.Component`.

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
  `wasmtime run`, and checks that it prints what `nanyx run` does.

## The console host's component root

`console.host.wasi` provides `Console`, `Env`, `FileSystem`, `Http`, `Clock`, `Random` and `Async` on WASI 0.3 alone, in
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
  block from it. Taking lines and the rest of the input out of the buffer is `console.stdin`'s, shared with the core
  root, which fills the buffer with WASI preview 1's `fd_read`.
- **Arguments and environment variables** are `list<string>` and `list<tuple<string, string>>`, returned in memory: an
  address and a count, each string an address and a length. The host allocates them with `cabi_realloc`, which is the
  program's allocator, and the root frees each once it has copied it out.
- **The clocks and random numbers** are `wasi:clocks/system-clock`'s `now`, an `instant` of seconds and nanoseconds
  written to memory, `monotonic-clock`'s `now`, nanoseconds as a `u64`, and `wasi:random`'s `get-random-u64`.
- **Exiting** is `wasi:cli/exit`'s `exit-with-code`, which takes 0 to 255; any other code exits with 1.
- **Files** are `wasi:filesystem`'s, in `console.host.wasi.files.nyx`, a `@partial` fragment of the root. The preopens
  come from `get-directories` once, and are kept in the host's block; a path is found in one as the core root finds it
  in WASI preview 1's (`within`, in `console.files`, which both roots share, with `bytesAt`). A file is opened with
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
  HTTP host's component root will share. A `Request` becomes a
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

A wait blocks: each async-lowered call that returns BLOCKED is waited on at once, in a waitable set of its own, as
the runtime's own writes are.
