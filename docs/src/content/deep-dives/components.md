---
title: "Components"
description: "How the compiler writes the core module of a WASI 0.3 component, and how the componentizer wraps it"
order: 17
---

The compiler can write a WebAssembly component that targets WASI 0.3, so that a program runs under Wasmtime, or any
runtime that implements WASI 0.3, and not only under `nanyx`'s own runtime. The design, and the stages still to come,
are on [Components](../work-in-progress/components) in Work in progress. So far the pieces below are built; no command
writes a component yet, which comes with the console host's component root
([#2237](https://github.com/nanyx-lang/nanyx/issues/2237)).

A component is built in two steps:

1. The backend writes a core module in wit-component's conventions (`WasmOptions.Component`).
2. The componentizer wraps it as a component of a world (`Nanyx.Runtime.Componentizer.componentize`).

# The core module

`WasmOptions.Component` changes three things about the module the backend writes (`ComponentAbi.fs`):

- **The entry is exported as `wasi:cli/run@0.3.0#run`,** not run when the module is instantiated. It calls the start
  function, if the program has one, and returns 0, which is `ok`. `run` is an `async func` in WIT, lifted synchronously:
  the instance blocks in each call it makes, as it does under `nanyx run`.
- **`cabi_realloc` is exported,** for the host to allocate in the module's memory through. It is the program's own
  allocator: a new block of the size asked for, with the old block's bytes copied in and the old block freed. Every
  block is aligned to 8 bytes, the most any WIT type needs.
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
