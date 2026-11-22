---
title: "Components"
description: "Design: `nanyx build` writes WASI 0.3 components, and the console and HTTP hosts target the standard wasi:* interfaces"
order: 4
---

This page is the design for [#2182](https://github.com/nanyx-lang/nanyx/issues/2182): emitting WebAssembly components
that target WASI 0.3, so that what `nanyx build` writes runs under Wasmtime, or any runtime that implements WASI 0.3, and
not only under `nanyx`'s own runtime. Stages 0 to 3 are built, and described on
[Components](../deep-dives/components) in Deep dives: every console program builds as a component, and every HTTP
handler as a `wasi:http/service` component. Stage 5 has begun: a program exports functions of numbers, strings,
records, tuples, lists and variants, marked `@component`, and imports other components' functions, marked `@import`
([A program's own exports](../deep-dives/components#a-programs-own-exports)). The page sets out what was found, the
[design](#design) the maintainer settled on 2026-11-21, what is still [open](#open-questions), and the [plan](#plan),
whose stages are filed as issues.

# Where things stand

**Today a built `.wasm` runs only under `nanyx run`.** It is a core module. The console host does its I/O through WASI
preview 1 (`wasi_snapshot_preview1`: `fd_write`, `path_open`, `clock_time_get` and the rest, in
`packages/nanyx.console/src/console.host.nyx` and `console.host.files.nyx`). It adds two of the runtime's own imports
where preview 1 has nothing:

- `nanyx:http`'s `send` and `receive`, which `Nanyx.Runtime.HttpImport` implements on .NET's `HttpClient`;
- for the HTTP host, `nanyx:serve`.

The compiler's own code imports `fd_write` and `proc_exit` too, to write a crash, a deferred error or `dbg`, and to exit.
A debug build that the runtime runs also imports `nanyx:debug`, for its probes and `dbg`. A build written to a file has
none of these ([Foreign functions](../deep-dives/foreign-functions), [Debug probes](../deep-dives/debug-probes)).

**Every wait blocks a thread.** A call such as `http.get` is a host call that blocks the instance's thread
([Concurrency and crash boundaries](./concurrency-and-crash-boundaries#how-it-runs)).

# What was found

All of this was checked on 2026-11-21, from the sources linked and from a spike that wasn't merged ([The spike](#the-spike)).

## WASI 0.3

- **0.3 is out, and on a release train.** [v0.3.0](https://github.com/WebAssembly/WASI/releases/tag/v0.3.0) was tagged
  on 2026-06-11, then 0.3.1 on 2026-08-11 and 0.3.2 on 2026-10-13. A new 0.3.x comes every two months
  ([Release.md](https://github.com/WebAssembly/WASI/blob/main/docs/Release.md)).
  - 0.3.1 adopted the component model's `map<K, V>` and made no change to WIT beyond a doc comment.
  - 0.3.2 changed only version numbers.
  - The interfaces a host needs have been stable since 0.3.0.
- **It has six proposals:** cli, clocks, filesystem, http, random and sockets.
- **WASI 0.3 is built on the component model's own async.** It uses `async func`, `stream<T>` and `future<T>`.
  - Each I/O operation is one `async func`, where 0.2 had a `pollable` and pairs of start and finish calls.
  - Bytes move through streams. For example, `wasi:cli/stdout` is `write-via-stream: func(data: stream<u8>) -> future<result<_, error-code>>`.
- **Wasmtime runs it by default.**
  - Wasmtime 46 is the first release that supports WASI 0.3.0 and enables the component model's async by default
    ([RELEASES.md](https://github.com/bytecodealliance/wasmtime/blob/release-46.0.0/RELEASES.md)).
  - The latest release is 51.0.0 (2026-11-20). It still vendors the WIT at `@0.3.0`. Its linker matches `0.3.x`
    versions semver-compatibly, so a component built against `@0.3.0` runs on any later 0.3 host.
  - `wasmtime run` looks for 0.3's `wasi:cli/run` export first. `wasmtime serve` runs the `wasi:http/service` world.
    Neither needs a flag.
  - Wasmtime 43 to 45 implement only a release candidate and can't run a 0.3.0 component
    ([component-docs](https://github.com/bytecodealliance/component-docs/blob/main/component-model/src/running-components/wasmtime.md)).

## Running components from .NET

**The embedding we use can't run a WASI 0.3 component, and the next version can't either.**

- **wasmtime-dotnet 48.0.2**, which `Nanyx.Compiler.Runtime` references, has no component API.
  - Its `Config` has only `WithComponentModel`.
  - The native library it ships does export Wasmtime's component C API (`wasmtime_component_*`), but it contains no
    WASI 0.3 at all. Every `wasi:` interface name in it is `@0.2.12`.
- **wasmtime-dotnet 50.0.1**, the latest on NuGet (2026-11-05), adds `Component`, `ComponentLinker`,
  `ComponentInstance`, `ComponentFunction` and `ComponentValue`.
  - `ComponentLinker` offers `AddWasiPreview2()` and nothing for 0.3.
  - Host functions are synchronous callbacks over `ComponentValue`, which has no stream or future.
  - The binding turns the component model's async off in every `Config`, because "Wasmtime's async implementation isn't
    compatible with .NET" ([commit 4aac1a5](https://github.com/bytecodealliance/wasmtime-dotnet/commit/4aac1a5958f4da2dadca4ae84e16004516372dd6)).
- **Wasmtime's C API (v51)** has components, `*_async` calls (Rust async on fibers, not the component model's streams),
  and `wasmtime_component_linker_add_wasip2` and `add_wasi_http`, both preview 2.
  - It has no WASI 0.3 adder, and no API for stream or future values
    ([headers](https://github.com/bytecodealliance/wasmtime/tree/v51.0.0/crates/c-api/include/wasmtime/component)).
  - Calling the C API directly instead of through wasmtime-dotnet would gain nothing for 0.3.
- **The `wasmtime` CLI** runs 0.3 components today, with no flags.

So, inside one .NET process, `nanyx run` can run core modules, and preview 2 components synchronously on
wasmtime-dotnet 50. Running a 0.3 component in-process would take one of these:

- adding WASI 0.3 to Wasmtime's C API and to wasmtime-dotnet, upstream;
- a native library of our own, in Rust on the `wasmtime` and `wasmtime-wasi` crates;
- the `wasmtime` CLI as a child process.

## What a component is, for our backend

A component is a wrapper around the core module the backend writes already. It holds:

- the component's types (its world);
- the core module itself;
- the canonical-ABI adapters (`canon lower` for each import, `canon lift` for each export);
- the wiring between them.

Nearly every language makes this wrapper the same way. It writes a core module that follows
[wit-component](https://github.com/bytecodealliance/wasm-tools/tree/main/crates/wit-component)'s naming conventions,
then has wit-component build the wrapper:

- componentize-dotnet, through `wasm-component-ld`;
- Go and MoonBit, through `wasm-tools component new`;
- componentize-py and jco, which link wit-component directly.

None of them that we looked at writes the component binary itself. The core module's half of the contract is:

- **Imports** named for the interface and function: module `wasi:cli/stdout@0.3.0`, name `write-via-stream`.
  - An import that is lowered async is prefixed `[async-lower]`.
  - The canonical built-ins are imports too: `[stream-new-0]write-via-stream`, `[stream-write-0]…`, `[future-drop-readable-1]…`,
    `[waitable-set-new]`, `[task-return]…`, `[subtask-drop]` and the rest
    ([validation.rs](https://github.com/bytecodealliance/wasm-tools/blob/main/crates/wit-component/src/validation.rs)).
- **Exports** named `wasi:cli/run@0.3.0#run`, plus `memory` and `cabi_realloc`, which the host allocates through. An export
  lifted with the async callback ABI is `[async-lift]…`, with its callback `[callback][async-lift]…`.
- **A `component-type` custom section** with the world's types, which wit-component writes from the WIT
  (`embed_component_metadata`).

At the boundary, each value is passed in the **canonical ABI**
([CanonicalABI.md](https://github.com/WebAssembly/component-model/blob/main/design/mvp/CanonicalABI.md)):

- a `string` is a pointer and a length in bytes, in UTF-8;
- a `list` is a pointer and a count;
- records and tuples are laid out field by field, aligned;
- a variant is a discriminant and then its payload;
- resources, streams and futures are `i32` handles.

A function with more than 16 flat parameters, or more than one flat result, passes them through memory.

**The callback (stackless) async ABI** works like this
([Concurrency.md](https://github.com/WebAssembly/component-model/blob/main/design/mvp/Concurrency.md#async-abi)):

- An export lifted `async (callback $cb)` gives its result by calling `task.return`, not by returning it. What it does
  return is an `i32`:
  - EXIT (0): done;
  - YIELD (1): run me again soon;
  - WAIT (2), with a waitable set's index in the upper 28 bits: call me back when something in that set is ready.
- The host then calls `$cb(event, p1, p2)` with what happened, such as a subtask returning, a stream read or write
  completing, or the task being cancelled. It keeps calling until the callback returns EXIT.
- An import lowered async returns at once, with a status and a subtask handle, and the caller joins the subtask to a
  waitable set.
- Between turns of this loop, the instance can run other calls into it.

This is the shape that compiling `Async` functions to state machines ([#1890](https://github.com/nanyx-lang/nanyx/issues/1890))
produces, as #2182 says. The scheduler that runs an instance's tasks returns WAIT when every task is waiting. The
callback is the scheduler resuming the task whose wait finished. The stackful ABI (async with no callback, where a
task blocks in `waitable-set.wait`) is the one that would need stack switching
([#2164](https://github.com/nanyx-lang/nanyx/issues/2164)), and nothing here needs it.

**A synchronous lift works too.** An `async func` export can be lifted without the async ABI, and the instance then
blocks in each call it makes, as the console host does today. The spike's component is lifted this way, so components
don't have to wait for #1890. One detail matters here:

- The synchronous forms of `stream.read` and `stream.write` are behind a feature that Wasmtime turns off by default.
- So a blocking read or write is written as the async form, followed straight away by `waitable-set.wait` on its
  handle. The spike shows that this runs by default.

## Tooling

- **wasm-tools 1.263 and wit-component 0.263** support every async name above, and validate what they build.
- **The preview 1 adapter targets 0.2 only.** `wasi_snapshot_preview1.wasm`, which `wasm-tools component new --adapt`
  uses to turn a preview 1 module into a component, is generated against `wasi:*@0.2.12`. There is no adapter from
  preview 1 to 0.3, so a host has to be written against 0.3 to get a 0.3 component.
- **No C API, and nothing in .NET.** wasm-tools's C API has nothing of wit-component's, and no .NET library encodes
  components. WACS.ComponentModel on NuGet has a WIT parser and a runtime, but we didn't evaluate it.
- **wit-component compiles to WebAssembly.** That is how jco ships wasm-tools to JavaScript, and it lets us run it
  ourselves (below).

## The spike

The spike was a hand-written core module for "hello" against `wasi:cli/command@0.3.0`. Its `run` was lifted
synchronously, and it wrote to stdout through `[stream-new-0]write-via-stream`, `write-via-stream` and
`[stream-write-0]write-via-stream`. Two builds of a 20-line Rust program on wit-component 0.263
(`embed_component_metadata`, then `ComponentEncoder`) wrapped it as a component:

- **Natively**, the program built the component, and validation passed. The wrapper took a 634-byte core module to a
  2,371-byte component, so it adds less than 2 KB.
- **Compiled to `wasm32-wasip1`**, the program was 2.2 MB. Run as a core module on wasmtime-dotnet 48 from F#, with WASI
  preview 1 and a preopened directory, it wrote a component **byte for byte the same** as the native build.
  - Compiling the 2.2 MB module took 1.7 s, which a serialized module (`Module.Serialize`) cached by the CLI would save.
  - Componentizing took 15 ms.

- **`wasmtime run` (51.0.0) runs it,** with one catch:
  - A *synchronous* `stream.write` is one of the component model's "more async built-ins". Wasmtime 51 turns these off by
    default, and refuses the component ("synchronous `stream.write` requires the component model more async builtins
    feature") unless it is run with `-W component-model-more-async-builtins`.
  - A second version lowered the write async, `[async-lower][stream-write-0]write-via-stream`. When that returned
    BLOCKED, it joined the stream to a new waitable set and called `[waitable-set-wait]`, all from the same
    synchronously lifted `run`. It printed its line under `wasmtime run` with default settings.

So the compiler can build components with nothing native beyond the Wasmtime it already embeds, and with
wit-component's validation.

# Design

## Scope: components from `nanyx build` only

**`nanyx build` writes components; every other tool keeps core modules.** `nanyx run`, `nanyx test`, the debugger and
the profiler run core modules on wasmtime-dotnet, as today, keeping probes, the profiler, named crash stacks, `dbg`
interleaving and a fresh instance per test or request. No embedding runs WASI 0.3 in a .NET process yet
([Running components from .NET](#running-components-from-net)), and this is the one choice that works today.

**Each host has two roots.** Its manifest names a component root and its world beside the core root:

```nanyx fragment
host = (
  root = "console.host"
  prelude = "console.prelude"
  component = (root = "console.host.wasi", world = "nanyx:console/command")
)
```

- **The component root is written against `wasi:*`.** It provides the contexts the core root does, which the compiler
  checks (`NYX0275`). Programs and libraries see only contexts, so they never know which root they ran on.
- **`nanyx build` writes a component** when the host has a component root, and `--core` writes a core module.
  `nanyx run` always runs the core root.
- **CI tests the component roots** by building the examples as components and running them under `wasmtime run` (46 or
  later) in the Compiler workflow. That is the one place a native Wasmtime CLI is needed.
- **The core roots go once an embedding runs 0.3 in process** (stage 6). Whether Wasmtime's C API and wasmtime-dotnet
  will add WASI 0.3 is a question for upstream; if the answer is no, or slow, a native library of our own on the `wasmtime` and
  `wasmtime-wasi` crates is the fallback.

Two alternatives were rejected: running components everywhere through the `wasmtime` CLI loses every in-process tool
and makes each user install Wasmtime, and a component-model runtime of our own in .NET duplicates Wasmtime.

## Which hosts, and which imports

Each import of the console and HTTP hosts maps onto a WASI 0.3 interface, or onto nothing:

| Import today | In a component | Notes |
|---|---|---|
| `fd_write`, `fd_read` on 0, 1, 2 (`Console`) | `wasi:cli/stdin`, `stdout`, `stderr` | Streams of bytes: write by `stream.write`, read by `stream.read` |
| `args_*`, `environ_*` (`Env`) | `wasi:cli/environment` | `get-arguments`, `get-environment` |
| `proc_exit` (`Env`, crashes) | `wasi:cli/exit` | `exit-with-code` |
| `path_open`, `fd_pread`, `fd_readdir`, … (`FileSystem`) | `wasi:filesystem/types`, `preopens` | `descriptor` is a resource. `read-via-stream`, and `read-directory` is a `stream<directory-entry>` |
| `clock_time_get` (`Clock`) | `wasi:clocks/monotonic-clock`, `system-clock` | `wait-for` is the `sleep` that `Clock` lacks |
| `random_get` (`Random`) | `wasi:random/random` | `get-random-bytes` |
| `nanyx:http` `send`, `receive` (`Http`) | `wasi:http/client` | `send: async func(request) -> result<response, error-code>`. Bodies are streams |
| `nanyx:serve` (HTTP host's entry) | export `wasi:http/handler` (world `wasi:http/service`) | `handle: async func(request) -> result<response, error-code>`. `wasmtime serve` runs it |
| `nanyx:debug` probes | none | Only a debug build the runtime runs has them, so never a component |
| `nanyx:debug` `dbg` | `wasi:cli/stderr` | A build written to a file already writes `dbg` to stderr itself |
| a terminal's raw mode and size (`Terminal`, not built) | none | WASI 0.3's `terminal-*` interfaces only say whether a stream is a terminal |

- **The console host's component root uses `wasi:*` alone.** Everything it provides has an equivalent. `dbg` writes to
  stderr, and probes never apply.
- **A context with no WASI equivalent is a compile error in a component build.** The component root doesn't provide
  it, so a program that needs it, such as `Terminal` once it is built, is an error naming the context when it is built
  as a component. That is the capability check doing its job, as for the browser host's missing `FileSystem`. There is
  no `nanyx:*` WIT interface for it, which would make the component non-portable without saying so.
- **The HTTP host's component root is in the world `wasi:http/service`,** with `Log` on stderr. It doesn't provide
  `Env`: the `service` world has no `wasi:cli/environment`, and importing it beyond the world would tie the component to
  hosts that happen to provide it. Configuration comes in through `init` (see [Serving HTTP](./hosts#serving-http)).
- **One request at a time per instance, and instances reused in turn.** `wasmtime serve` lets a 0.3 component handle
  up to 16 requests at once in one instance, which would have them share its memory, and so its crashes. So the
  component raises backpressure (`backpressure.inc`) while it serves a request, and the host gives it no second one
  until it is done. It doesn't stop the host reusing the instance for the next request, which `wasmtime serve` does up
  to 128 times: the program runs afresh for each, its cached values set up and torn down by its start function, so
  nothing it computes carries over, but a request that crashes ends the instance. That is the default; strict isolation
  is `wasmtime serve --max-instance-reuse-count 1`, a fresh instance per request, as `nanyx run` gives (settled
  2026-11-22).
- **The runtime's own imports follow the target.** On a component, the compiler's crash, deferred-error and `dbg` writes
  use `wasi:cli/stderr`, and its exits use `wasi:cli/exit`, not `fd_write` and `proc_exit`.
- **The console host's world is its own, `nanyx:console/command`:** `wasi:cli/command@0.3.0` with
  `wasi:http/client@0.3.0`, which `wasi:cli/command` doesn't import. A component imports only what the program uses, so
  one that makes no requests is a `wasi:cli/command` component; one that does needs a runtime with `wasi:http`, as
  `wasmtime run -S http` is.
- **The WIT is `@0.3.0`.** Every 0.3 host accepts it, since later 0.3.x versions are compatible, and nothing a host needs
  changed after it.
- **The playground keeps its own host.** Browsers don't run components, so `nanyx.playground` is unaffected
  ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)).

## The backend

**What the compiler emits is still a core module,** in wit-component's conventions. The wrapper is built from it.

**wit-component ships as WebAssembly** and runs on the Wasmtime the runtime already embeds, as the spike did.

- It is built from a small Rust crate in the repository to `wasm32-wasip1` by Cargo when the runtime builds, and embedded in
  `Nanyx.Compiler.Runtime`. Rust is needed to build the CLI, but not to use it.
- It adds about 2 MB to the CLI, plus a one-off compile that is cached as a serialized module.
- wit-component validates what it builds and keeps up with the spec; we follow the spec by bumping a crate.

Requiring `wasm-tools` on the user's PATH was rejected as one more thing to install at a matching version, and an
encoder of our own in F# as a few thousand lines kept in step with a moving spec, with no validator of our own. An F#
encoder can still replace wit-component later, once the format is settled, with wit-component's output to test it.

**The canonical ABI is lowered by hand in the host packages,** in Nanyx on `nanyx/host.memory`, as the console host
lowers WASI preview 1 today.

- An `@extern` already names any module and function, so `@extern("wasi:cli/stdout@0.3.0", "[stream-write-0]write-via-stream")`
  is an ordinary extern.
- Every lowered signature is `i32` and `i64`, which externs already take.
- A string is the UTF-8 bytes a Nanyx string already holds, so it goes out as an address and a length. A string that
  comes back is copied out of a buffer the host allocated through `cabi_realloc`, as the console host copies out of a
  preview 1 buffer.

The compiler generates lifting and lowering only for user components (stage 5), where Nanyx types cross a boundary a
user declares: a record as a record, a tag union as a variant, `list` as `list`, `string` as `string`.

**What the compiler adds:**

- **A `cabi_realloc` export** on the allocator: `(old, oldSize, align, newSize) -> address`.
- **The entry exported under the world's name.** It is `wasi:cli/run@0.3.0#run`, returning 0 for `ok`. The playground
  build already exports the module's start (`ExportStart`), so this is a name and a result away from it.
- **The runtime's own imports on stderr and exit**, as above.
- **The world's WIT,** shipped in the host package (a `wit/` folder, with WASI 0.3's WIT under `wit/deps`), and given to
  the componentizer with the world's name.

## Async: a synchronous lift first, the callback ABI after #1890

- **First**, `run` is lifted synchronously. `handle` was to be too, until stage 3 found it can't be (below).
  - A call that waits, or a stream read or write, is lowered async and waited on at once with `waitable-set.wait`,
    which runs with Wasmtime's default settings. A synchronous `stream.write` doesn't.
  - A wait blocks the instance, as it blocks its thread today.
  - The host package writes this "start, then wait" pair once per kind of call. It is the same code that #1890's
    scheduler later splits in two.
- **After [#1890](https://github.com/nanyx-lang/nanyx/issues/1890)'s state machines**, an `Async` entry is lifted
  `[async-lift]` with the scheduler as its `[callback][async-lift]`.
  - A call that waits becomes an `[async-lower]` import, or an async `stream.read` or `stream.write`, whose subtask the
    task joins to the instance's waitable set.
  - When every task is waiting, the scheduler returns WAIT. When the callback gets an event, the scheduler resumes the
    task that was waiting on it.
  - `start!` and `race` need nothing more from the host. Cancelling a task cancels its subtasks (`subtask.cancel`), and
    `TASK_CANCELLED` arriving at the callback is how the host cancels the entry.
- **Stage 1 doesn't depend on #1890 or #2164.** A synchronously lifted component is the same program model as today.
- **`wasi:http/service` amends this for its entry (settled 2026-11-22).** Its `handle` is lifted async with the
  callback ABI from stage 3 on, not synchronously: the host reads the response's body from a stream only after
  `handle` returns, so a synchronous `handle` can't write a body bigger than the stream's buffer. The compiler
  generates a small wrapper for that entry alone, which runs the program as synchronously as a `run` does, returns
  the response with `task.return`, and then writes the body ([The handler](../deep-dives/components#the-handler)).
  #1890's scheduler replaces the wrapper in stage 4.
- **#2164 isn't needed for components at all.** Stack switching would make the stackful ABI possible, but the callback
  ABI does the same job with the state machines. #2164 stays about running tasks on stacks.

## User components (stage 5)

Settled by the maintainer on 2026-11-22 ([#2241](https://github.com/nanyx-lang/nanyx/issues/2241)):

- **The WIT is generated from Nanyx declarations.** Nanyx is the source of truth; no WIT is written by hand or read.
- **Exports are marked per def,** `@component export def area: Shape -> float`, the interface being the def's module.
  Names are written in kebab case: `areaOf` is `area-of`, the module `geo.shapes` the interface `geo-shapes`.
- **Imports are bodiless defs, as `@extern` is:** `@import("acme:geometry/shapes@1.0.0", "area") def area: Shape -> float`,
  naming the interface's full WIT name and the function, whose name defaults to the def's, kebab-cased. The WIT of the
  imported interface is generated from these declarations too: component types are checked structurally, by field
  and case names, so no foreign WIT is read. Under `nanyx run` and `nanyx test`, which run core modules, an imported
  def reachable from the entry or a test is an error naming it.
- **`nanyx build` writes `out/<name>.wasm` and `out/wit/`,** a complete WIT directory: the program's package, with a
  world that includes the host's and exports and imports the program's interfaces, and under `deps/` the packages it
  uses. A package with exports and no `@entry` is a library component, with no `run`.
- **The WIT package is named from the manifest:** `acme.shapes` 0.1.0 is `acme:shapes@0.1.0`, and `greeter` is
  `greeter:greeter@0.1.0`.
- **Types are written literally.** Scalars, strings, lists, records and tuples map directly. Every tag union is a WIT
  `variant` with the same case names, `#some`/`#none` and `#ok`/`#error` included (no `option` or `result`), and nominal
  types are written out structurally. A named generic type is named with its arguments appended (`Result(string, int)`
  is the variant `result-string-s64`). A function, a context, a type variable, and a record or tag union with no name
  in an exported or imported signature are a compile error, asking for a `type` where one would do.
- **An export may require only contexts the host's component root provides,** which it runs under, as the entry does;
  any other is `NYX0237`.

# Open questions

- **Requests at once in one instance.** An instance serves one request at a time, and others in turn. Whether requests
  may interleave in one instance, and so share its memory and crashes, once the callback ABI lets them, belongs with
  tasks and processes on [Concurrency and crash boundaries](./concurrency-and-crash-boundaries#3-tasks-and-processes).
- **Exporting a module the program doesn't import.** A program exports the `@component` defs of the modules it
  imports, directly or not; a library component, which has no entry to import them, will need a root that imports
  each module with one.
- **Running 0.3 in process (stage 6).** Whether Wasmtime's C API and wasmtime-dotnet will add WASI 0.3, or we build a
  native library of our own.

# Plan

Each stage is an issue. A stage is marked done here when its issue closes.

| Stage | What it builds | What users can do then |
|---|---|---|
| 0. The componentizer ([#2236](https://github.com/nanyx-lang/nanyx/issues/2236)), done | A small Rust crate on wit-component, built to `wasm32-wasip1` by Cargo when the runtime builds, and embedded in `Nanyx.Compiler.Runtime`. A `cabi_realloc` export. The entry exported under a world's name. The runtime's own imports on `wasi:cli` | Nothing visible yet |
| 1a. Console components ([#2237](https://github.com/nanyx-lang/nanyx/issues/2237)), done | `nanyx.console`'s component root on `wasi:cli`, `wasi:clocks` and `wasi:random`, lifted synchronously. `nanyx build` writes a component, `--core` a module. CI runs the example packages under `wasmtime run` | Build a command-line program that needs no `FileSystem` or `Http` once, and run it with `wasmtime run` or any WASI 0.3 runtime |
| 1b. Files in components ([#2237](https://github.com/nanyx-lang/nanyx/issues/2237)), done | `FileSystem` on `wasi:filesystem`, in the console host's component root | The same, for programs that read and write files |
| 2. HTTP client ([#2238](https://github.com/nanyx-lang/nanyx/issues/2238)), done | `Http` on `wasi:http/client`, with bodies over streams | Programs that make requests run outside `nanyx` too |
| 3. HTTP service ([#2239](https://github.com/nanyx-lang/nanyx/issues/2239)), done | `nanyx.http`'s component root exports `wasi:http/handler` (world `wasi:http/service`), one request at a time per instance, no `Env` | Deploy a handler with `wasmtime serve`, or to a host that runs `wasi:http` components |
| 4. Async components ([#2240](https://github.com/nanyx-lang/nanyx/issues/2240), after [#1890](https://github.com/nanyx-lang/nanyx/issues/1890)) | Entries lifted with the callback ABI, waits as async lowers joined to a waitable set, cancellation | `start!` and `race` run concurrently in a component, with no thread per wait |
| 5. User components ([#2241](https://github.com/nanyx-lang/nanyx/issues/2241)), begun | WIT for a program's own exports and imports, and the canonical ABI generated for Nanyx types. Built: exports and `@import`s of numbers, bools, strings, records, tuples, lists and variants, with `out/wit`, and a test in CI with Rust components under `wasmtime run`. Still to come: exports that require contexts; library components | Write plugins and libraries that other languages' components call, and call theirs |
| 6. One root per host ([#2242](https://github.com/nanyx-lang/nanyx/issues/2242)) | Run 0.3 components in `nanyx run`, once an embedding supports it (upstream or our own), and delete the core roots | One implementation of each host |
