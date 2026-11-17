---
title: "Debugging"
description: "dbg, crash stacks, a debugger built on probes, recording and replaying a run, and profiling"
order: 8
---

Today Nanyx has [`dbg`](../stdlib/console#dbg), which prints any value after where it was called and which a release
build leaves out, and [deferred errors](../standard-tools/errors) that report their span when they're reached. There is
no debugger and no source mapping. This page is the plan, agreed with the maintainer on 2026-11-17 and filed in stages
([Plan](#plan)).

# Decisions

- **`dbg` prints where it was called,** `[main.nyx:12] 84`, but not the expression's text, which for a `dbg` in the
  middle of a pipeline would be the whole pipeline. It still returns its value. **A release build strips it:**
  `dbg(x)` compiles to `x`, so a `dbg` left in code costs nothing in release, and there's no linter warning for one.
- **A release build keeps function names and spans.** A table from code offsets to source spans, beside the Wasm name
  section, lets a crash in a release build report a stack of `name (file:line)` frames. No probes and no locals in
  release; the cost is module size only.
- **The debugger is built on probes, not DWARF.** A debug build calls a host import at each statement boundary and at
  each function's entry and return. The runtime keeps a shadow stack from them, stops at breakpoints, and, when
  stopped, asks a frame for its locals, written by the same type-directed printer `dbg` uses. The `nanyx` runtime then
  speaks the Debug Adapter Protocol to VS Code. Everything is code the project owns: it works on Windows, any host can
  answer the import, and values show the way Nanyx writes them. DWARF line tables may come later, for native profilers
  and crash dumps, but describing Nanyx's values to LLDB isn't planned.
- **Time-travel debugging is a goal, designed in from the start.** Every effect goes through a context a host
  provides, so a run is fully determined by what those context members returned. Recording them, and replaying a run
  by answering each call from the recording, gives deterministic repros; with probes marking steps, the debugger can
  step backwards by replaying to the step before. Immutable values shared by reference counts make snapshots cheap.
- **The console host first.** Debugging a request in the HTTP host, and a single test from the editor, come next; the
  browser host later.

# Open questions

- **How a frame's locals stay reachable** for the debugger to read: spilled to a shadow frame in linear memory at each
  probe, or passed to the probe. Settled, with measurements, in
  [#2040](https://github.com/nanyx-lang/nanyx/issues/2040).
- **The recording's format,** how tasks interleave in it once async has them, and whether users keep recordings
  ([#2042](https://github.com/nanyx-lang/nanyx/issues/2042)).
- **Stepping backwards:** how often to snapshot, and how the adapter presents it
  ([#2043](https://github.com/nanyx-lang/nanyx/issues/2043)).
- **Evaluating an expression at a breakpoint** means compiling a snippet against the paused frame's scope. Not
  planned yet.
- **Leak checks:** a debug run reporting reference-counted values still alive at exit, on top of the memory report.

# Plan

| Step | Issue | After |
|---|---|---|
| `dbg` prints its location; release builds strip it (done) | [#2038](https://github.com/nanyx-lang/nanyx/issues/2038) | |
| Names and spans kept in release; crash stacks with file and line | [#2039](https://github.com/nanyx-lang/nanyx/issues/2039) | |
| Debug probes, the shadow stack and locals | [#2040](https://github.com/nanyx-lang/nanyx/issues/2040) | |
| The debug adapter: breakpoints, stepping and variables in VS Code | [#2041](https://github.com/nanyx-lang/nanyx/issues/2041) | #2040 |
| Recording a run's effects and replaying it (Planning) | [#2042](https://github.com/nanyx-lang/nanyx/issues/2042) | |
| Stepping backwards (Planning) | [#2043](https://github.com/nanyx-lang/nanyx/issues/2043) | #2041, #2042 |
| `nanyx run --profile`, with Wasmtime's guest profiler | [#2044](https://github.com/nanyx-lang/nanyx/issues/2044) | |
