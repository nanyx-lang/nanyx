---
title: "Debugging"
description: "dbg, crash stacks, a debugger built on probes, recording and replaying a run, and profiling"
order: 8
---

Today Nanyx has [`dbg`](../stdlib/console#dbg), which prints any value after where it was called and which a release
build leaves out, and [deferred errors](../standard-tools/errors) that report their span when they're reached. A crash, a
deferred error and a runtime error print the stack, with the file and line of each frame, in debug and release builds
alike ([Crash stacks](../compiler-internals/crash-stacks)). A program `nanyx run` runs has debug probes, which a test
host can already follow with a shadow stack and read a frame's locals from
([Debug probes](../compiler-internals/debug-probes)), but there is no debugger yet. This page is the plan, agreed with
the maintainer on 2026-11-17 and filed in stages ([Plan](#plan)).

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
- **The host decides where `dbg` output goes** (done, [#2058](https://github.com/nanyx-lang/nanyx/issues/2058)): `dbg`
  needs no context, but at run time it gives its location and value to the runtime's `nanyx:debug` `dbg`. The console
  host writes it to stderr and the HTTP host to its log with the request it came from
  ([Where `dbg` prints](../advanced/hosts#where-dbg-prints)); the test runner will keep it with the test that printed it,
  once there is one ([#2103](https://github.com/nanyx-lang/nanyx/issues/2103)).
- **`dbg` values show in the editor, not the terminal,** for runs the editor starts: the runtime writes each `dbg` hit
  to a channel (`nanyx run --dbg-to`), and the extension shows the value at the end of its line, with a count and a
  list of every hit for one hit several times. A `nanyx run` in a terminal prints as before, to stderr.
- **Live mode is gated on the entry's contexts.** The editor re-runs on save, notebook-style, only when the entry needs
  nothing beyond `Console`: Nanyx knows from the type that re-running can't write files or send requests. A program
  that needs more is re-run on save only if the user turns it on for that file.
- **The console host first.** Debugging a request in the HTTP host, and a single test from the editor, come next; the
  browser host later.
- **A frame's locals are written when the runtime asks,** at the probe, through the `dbg` printers, rather than spilled
  to a shadow frame at each probe: a spilled reference could outlive the value, which reference counting gives up after
  its last use, and keeping it alive would change every debug run's memory. Measured in
  [#2040](https://github.com/nanyx-lang/nanyx/issues/2040), on [Debug probes](../compiler-internals/debug-probes).

# Open questions

- **The locals of a frame further out,** which the debugger shows when the user picks one: the probe writes only the
  locals of the frame it is in. The adapter can ask at each statement probe while it steps, and keep each frame's last,
  or the program could write them on request as it returns
  ([#2041](https://github.com/nanyx-lang/nanyx/issues/2041)).
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
| Names and spans kept in release; crash stacks with file and line (done) | [#2039](https://github.com/nanyx-lang/nanyx/issues/2039) | |
| Debug probes, the shadow stack and locals (done) | [#2040](https://github.com/nanyx-lang/nanyx/issues/2040) | |
| The debug adapter: breakpoints, stepping and variables in VS Code | [#2041](https://github.com/nanyx-lang/nanyx/issues/2041) | #2040 |
| Recording a run's effects and replaying it (Planning) | [#2042](https://github.com/nanyx-lang/nanyx/issues/2042) | |
| Stepping backwards (Planning) | [#2043](https://github.com/nanyx-lang/nanyx/issues/2043) | #2041, #2042 |
| `nanyx run --profile`, with Wasmtime's guest profiler (done) | [#2044](https://github.com/nanyx-lang/nanyx/issues/2044) | |
| Each function's file and line in a profile (done) | [#2072](https://github.com/nanyx-lang/nanyx/issues/2072) | #2044 |
| `dbg` output handled by the host (done) | [#2058](https://github.com/nanyx-lang/nanyx/issues/2058) | |
| `dbg` output kept with each test by the test runner (Planning) | [#2103](https://github.com/nanyx-lang/nanyx/issues/2103) | `nanyx test` |
| `dbg` values inline in the editor | [#2054](https://github.com/nanyx-lang/nanyx/issues/2054) | #2058 |
| Live mode: re-run on save when the entry needs only `Console` | [#2055](https://github.com/nanyx-lang/nanyx/issues/2055) | #2054 |
