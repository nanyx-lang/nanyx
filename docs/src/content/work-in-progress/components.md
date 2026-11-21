---
title: "Components"
description: "Proposal: `nanyx build` writes WASI 0.3 components, and the console and HTTP hosts target the standard wasi:* interfaces"
order: 4
---

This page answers the open questions on [#2182](https://github.com/nanyx-lang/nanyx/issues/2182): emitting WebAssembly
components that target WASI 0.3, so that what `nanyx build` writes runs under Wasmtime, or any runtime that implements
WASI 0.3, and not only under `nanyx`'s own runtime. Nothing here is built. The page sets out what was found, the options
for each question and a recommendation for each, and a staged plan. The decisions are the maintainer's.

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

# Open questions, options and recommendations

## 1. Scope: where components are written, and how `nanyx run` runs them

**Options:**

- **A. Components from `nanyx build` only.** `nanyx run`, `nanyx test`, the debugger and the profiler keep running core
  modules on wasmtime-dotnet. A host package gives a second root module for the component, written against `wasi:*`.
  This works today. The cost is two implementations of each host's contexts, each a few hundred lines of Nanyx.
- **B. Components everywhere, run by the `wasmtime` CLI.** `nanyx run` starts `wasmtime run`. Several things are lost
  or would need redoing outside the process:
  - the debugger's probes (`nanyx:debug`);
  - the profiler (`GuestProfiler`);
  - crash stacks named from the module's table of places;
  - `dbg` interleaved by the runtime;
  - the test runner's and HTTP host's fresh instance per test or request, in process.

  Every user would also need Wasmtime 46 or later installed, or downloaded by `nanyx`.
- **C. Components everywhere, on a native library of our own.** A small Rust library on `wasmtime` and `wasmtime-wasi`
  (with WASI 0.3 and `wasi:http` 0.3) that the runtime calls through a C ABI, keeping probes and profiling in process.
  It adds Rust to the build and the release matrix (Linux, Windows and macOS on x64 and arm64), and we would own that
  binding. Doing the same upstream, in Wasmtime's C API and wasmtime-dotnet, is the same work done for everyone, but on
  their schedule.
- **D. The runtime implements the lowered imports itself.** The runtime would instantiate the inner core module and
  provide `[stream-new-0]…` and the rest in .NET: a component-model runtime of our own. This option is rejected: it is
  the largest job of the four, and it duplicates Wasmtime.

**Recommendation: A now, then remove the core roots once an embedding runs 0.3 in process.**

- A keeps every tool working and delivers the portable output, which is what #2182 is for.
- The duplication stays in the hosts. Programs and libraries see only contexts, so they never know which root they ran
  on.
- File the upstream question, whether Wasmtime's C API and wasmtime-dotnet will add WASI 0.3, and revisit C if the answer
  is no, or slow.
- Test the component roots by building the examples as components and running them under `wasmtime run` in the Compiler
  workflow. That is the one place a native Wasmtime CLI is needed.

**How `nanyx build` chooses.** The host's manifest names a component root and its world, beside the core root:

```nanyx fragment
host = (
  root = "console.host"
  prelude = "console.prelude"
  component = (root = "console.host.wasi", world = "wasi:cli/command@0.3.0")
)
```

- `nanyx build` writes a component when the host has a component root, and `--core` writes a core module.
- `nanyx run` always runs the core root.
- The two roots must provide the same contexts, which the compiler can check.

## 2. Which hosts, and which imports

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

**Recommendation:**

- **The console host's component root uses `wasi:*` alone.** Everything it provides has an equivalent. `dbg` writes to
  stderr, and probes don't apply.
- **A context with no WASI equivalent isn't provided by the component root.** When `Terminal` is built, the component
  root doesn't provide it, so a program that needs it is a compile error naming `Terminal` when it is built as a
  component. That is the capability check doing its job, as for the browser host's missing `FileSystem`. A
  `nanyx:terminal` WIT interface, which only our runtime implements, would make the component non-portable without
  saying so.
- **The HTTP host moves onto `wasi:http/service`,** with `Log` on stderr. `Env` imports `wasi:cli/environment`, which
  the `service` world doesn't include, so the HTTP host's component root would either import it beyond the world, which
  `wasmtime serve` provides but other hosts may not, or not provide `Env`. Of the two, not providing `Env` is the more
  honest, and configuration then comes in through `init` (see [Serving HTTP](./hosts#serving-http)).
- **The runtime's own imports follow the target.** On a component, the compiler's crash, deferred-error and `dbg` writes
  use `wasi:cli/stderr`, and its exits use `wasi:cli/exit`, not `fd_write` and `proc_exit`.
- **Target `@0.3.0`.** Every 0.3 host accepts it, since later 0.3.x versions are compatible, and nothing a host needs
  changed after it.
- **The playground keeps its own host.** Browsers don't run components, so `nanyx.playground` is unaffected
  ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)).

**Requests at once in one instance.** `wasmtime serve` lets a 0.3 component handle many requests at once: its
concurrency default is 128 for 0.3 against 1 for 0.2. That conflicts with the HTTP host's model of a fresh instance per
request, where a crash ends only that request.

- With a synchronous lift, the instance is locked for the whole call, so it handles one request at a time.
- With the callback ABI, requests would share one instance's memory, and so share its crashes.
- `backpressure.inc` lets the component refuse a second request while it serves one.

This page doesn't settle it: it belongs with tasks and processes on
[Concurrency and crash boundaries](./concurrency-and-crash-boundaries#3-tasks-and-processes).

## 3. The backend work

**What the compiler emits is still a core module,** in wit-component's conventions. The wrapper is built from it.

**How the wrapper is built. Options:**

- **A. Ship wit-component compiled to WebAssembly,** and run it on the Wasmtime the runtime already embeds, as the spike
  did.
  - It adds 2.2 MB to the CLI, plus a one-off compile that can be cached.
  - wit-component validates what it builds, and keeps up with the spec. Wasmtime 50, for instance, tracked a change to
    the async built-ins' immediates. We follow the spec by bumping a crate.
  - The `.wasm` is built from a small Rust crate in the repository, by CI, and embedded in `Nanyx.Compiler.Runtime`.
    Rust is then needed to build the CLI, but not to use it.
- **B. Require `wasm-tools` on the user's PATH.** No build change, but users have one more thing to install, at a
  version that matches.
- **C. Write the encoder in F#.** It would cover the component binary format's type, import, core-module, canon,
  instance and export sections, encoding the world's WIT types, and either a WIT parser or a fixed encoding of each
  host's world.
  - It has no dependency, and it works in the browser build. The browser doesn't need components, though.
  - It is a few thousand lines that we would keep in step with a spec still gaining features, with no validator of our
    own. Tests would still need wasm-tools to check what it writes.

**Recommendation: A.** C can replace it later if the dependency is ever a problem. The format would by then be settled,
and A's output can test it.

**Canonical ABI. Recommendation: by hand in the host packages first, generated by the compiler later.**

- **For the hosts' imports**, the host package lowers by hand in Nanyx, on `nanyx/host.memory`, as the console host
  lowers WASI preview 1 today.
  - An `@extern` already names any module and function, so `@extern("wasi:cli/stdout@0.3.0", "[stream-write-0]write-via-stream")`
    is an ordinary extern.
  - Every lowered signature is `i32` and `i64`, which externs already take.
  - A string is the UTF-8 bytes a Nanyx string already holds, so it goes out as an address and a length. A string that
    comes back is copied out of a buffer the host allocated through `cabi_realloc`, as the console host copies out of a
    preview 1 buffer.
- **For Nanyx types at a boundary that a user declares**, the compiler generates the lifting and lowering: a record as a
  record, a tag union as a variant, `list` as `list`, `string` as `string`. This is only needed for user-facing
  components (stage 5 of the [Plan](#plan)), such as a plugin that exports functions of its own types.

**What the compiler needs for stage 1:**

- **A `cabi_realloc` export** on the allocator: `(old, oldSize, align, newSize) -> address`.
- **The entry exported under the world's name.** It is `wasi:cli/run@0.3.0#run`, returning 0 for `ok`. The playground
  build already exports the module's start (`ExportStart`), so this is a name and a result away from it.
- **The runtime's own imports on stderr and exit**, as above.
- **The world's WIT,** shipped in the host package (a `wit/` folder, with WASI 0.3's WIT under `wit/deps`), and given to
  the componentizer with the world's name.

**Async. Recommendation: a synchronous lift first, and the callback ABI with #1890.**

- **First**, `run` and `handle` are lifted synchronously.
  - A call that waits, or a stream read or write, is lowered async and waited on at once with `waitable-set.wait`,
    which runs with Wasmtime's default settings. A synchronous `stream.write` doesn't.
  - A wait blocks the instance, as it blocks its thread today.
  - The host package writes this "start, then wait" pair once per kind of call. It is the same code that #1890's
    scheduler later splits in two.
- **With #1890**, an `Async` entry is lifted `[async-lift]` with the scheduler as its `[callback][async-lift]`.
  - A call that waits becomes an `[async-lower]` import, or an async `stream.read` or `stream.write`, whose subtask the
    task joins to the instance's waitable set.
  - When every task is waiting, the scheduler returns WAIT. When the callback gets an event, the scheduler resumes the
    task that was waiting on it.
  - `start!` and `race` need nothing more from the host. Cancelling a task cancels its subtasks (`subtask.cancel`), and
    `TASK_CANCELLED` arriving at the callback is how the host cancels the entry.

## 4. When

- **Stage 1 doesn't depend on #1890 or #2164.** A synchronously lifted component is the same program model as today.
- **The callback ABI comes after #1890's state machines,** and uses them unchanged. That is the reason to lift
  synchronously first rather than wait.
- **#2164 isn't needed for components at all.** Stack switching would make the stackful ABI possible, but the callback
  ABI does the same job with the state machines. #2164 stays about running tasks on stacks.

# Plan

Each stage is a separate issue, to be filed once the maintainer settles the questions above.

| Stage | What it builds | What users can do then |
|---|---|---|
| 0. The componentizer | A small Rust crate on wit-component, built to `wasm32-wasip1` by CI and embedded in `Nanyx.Compiler.Runtime`. A `cabi_realloc` export. The entry exported under a world's name. The runtime's own imports on `wasi:cli` | Nothing visible yet |
| 1. Console components | `nanyx.console`'s component root on `wasi:cli`, `wasi:filesystem`, `wasi:clocks` and `wasi:random`, lifted synchronously. `nanyx build` writes a component, `--core` a module. CI runs the examples under `wasmtime run` | Build a command-line program once, and run it with `wasmtime run` or any WASI 0.3 runtime |
| 2. HTTP client | `Http` on `wasi:http/client`, with bodies over streams | Programs that make requests run outside `nanyx` too |
| 3. HTTP service | `nanyx.http`'s component root exports `wasi:http/handler` (world `wasi:http/service`) | Deploy a handler with `wasmtime serve`, or to a host that runs `wasi:http` components |
| 4. Async components (after [#1890](https://github.com/nanyx-lang/nanyx/issues/1890)) | Entries lifted with the callback ABI, waits as async lowers joined to a waitable set, cancellation | `start!` and `race` run concurrently in a component, with no thread per wait. A service handles many requests per instance, if that is allowed (see [Requests at once](#2-which-hosts-and-which-imports)) |
| 5. User components | WIT for a program's own exports and imports, and the canonical ABI generated for Nanyx types | Write plugins and libraries that other languages' components call, and call theirs |
| 6. One root per host | Run 0.3 components in `nanyx run`, once an embedding supports it (upstream or our own), and delete the core roots | One implementation of each host |
