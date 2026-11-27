---
title: "Debugging"
description: "A debugger built on probes, dbg in the editor, recording and replaying a run, and what is still open"
order: 8
---

[`dbg`](../stdlib/console#dbg), [crash stacks](../deep-dives/crash-stacks), [debug probes](../deep-dives/debug-probes)
and [profiling](../deep-dives/command-line#profiling) with `nanyx run --profile` are built, and described on those pages;
where `dbg` prints is the host's choice ([Where `dbg` prints](../advanced/hosts#where-dbg-prints)). This page is the
plan for the rest, agreed with the maintainer on 2026-11-17 and filed in stages ([Plan](#plan)).

# Decisions

- **The debugger is built on probes, not DWARF.** A build with probes calls a host import at each statement boundary
  and at each function's entry and return, and the runtime keeps a shadow stack from them and asks a frame for its
  locals ([Debug probes](../deep-dives/debug-probes)). Only the debugger listens to them, so it compiles the program
  with them when it attaches to a run; `nanyx run` and `nanyx test` build without them, and no flag asks for them
  ([#2225](https://github.com/nanyx-lang/nanyx/issues/2225)). The runtime will stop at breakpoints and speak the Debug Adapter
  Protocol to VS Code. Everything is code the project owns: it works on Windows, any host can answer the import, and
  values show the way Nanyx writes them. DWARF line tables may come later, for native profilers and crash dumps, but
  describing Nanyx's values to LLDB isn't planned.
- **Time-travel debugging is a goal, designed in from the start.** Every effect goes through a context a host
  provides, so a run is fully determined by what those context members returned. Recording them, and replaying a run
  by answering each call from the recording, gives deterministic repros; with probes marking steps, the debugger can
  step backwards by replaying to the step before. Immutable values shared by reference counts make snapshots cheap.
- **The test runner keeps `dbg` output with the test that printed it,** once there is one
  ([#2103](https://github.com/nanyx-lang/nanyx/issues/2103)).
- **`dbg` values show in the editor, not the terminal,** for runs the editor starts: the runtime writes each `dbg` hit
  to a channel (`nanyx run --dbg-to`), and the extension shows the value at the end of its line, with a count and a
  list of every hit for one hit several times. A `nanyx run` in a terminal prints to stderr.
- **Live mode is gated on the entry's contexts.** The editor re-runs on save, notebook-style, on its own when the entry
  needs nothing beyond `Console`, `Async` and `FileSystem`, whose files a live run keeps in memory over the real ones
  (`nanyx run --files-in-memory`), with no input: Nanyx knows from the type that re-running can't reach past the editor.
  A program that needs more is re-run on save only once the user turns it on for its script or package, from the Live
  mode view in the Nanyx sidebar, and then with those contexts for real; its files are still in memory. A save stops a
  run still going, and runs have a time limit.
- **Live mode can show every value, not only `dbg`'s.** A toggle switches it between `dbg` values only, the default,
  and the value of each `def` and expression statement at the end of its line. Showing every value needs a live run
  built with probes, or a cheaper write of its own at each binding
  ([#3198](https://github.com/nanyx-lang/nanyx/issues/3198)).
- **The console host first.** Debugging a request in the HTTP host, and a single test from the editor, come next; the
  browser host later.

# Open questions

- **The locals of a frame further out,** which the debugger shows when the user picks one: the probe writes only the
  locals of the frame it is in. The adapter can ask at each statement probe while it steps, and keep each frame's last,
  or the program could write them on request as it returns
  ([#2041](https://github.com/nanyx-lang/nanyx/issues/2041)).
- **The recording's format,** how tasks interleave in it once async has them, and whether users keep recordings
  ([#2042](https://github.com/nanyx-lang/nanyx/issues/2042)).
- **Stepping backwards:** how often to snapshot, and how the adapter presents it
  ([#2043](https://github.com/nanyx-lang/nanyx/issues/2043)).
- **Every local of a long function.** A statement's probe writes the parameters and the 64 locals bound last (see
  [Debug probes](../deep-dives/debug-probes)), so a frame with more in scope shows only those. To show them all and stay
  linear, a probe could write each local once, where it is bound, with the runtime's frame keeping its text until its
  block ends, and a mutable object written again when it changes; that changes `nanyx:debug`, the runtime and the
  adapter, and has a debug session write every local whether it stops there or not.
- **Evaluating an expression at a breakpoint** means compiling a snippet against the paused frame's scope. Not
  planned yet.
- **Leak checks:** a debug run reporting reference-counted values still alive at exit, on top of the memory report.
- **Traces,** as in Dark: keep the recorded calls of a function, an HTTP handler's requests among them, pick one in the
  editor to see its values, and replay it through the code after an edit. A run is determined by what its contexts
  returned, so a replay is exact and has no effects, whatever contexts the function needs
  ([#3199](https://github.com/nanyx-lang/nanyx/issues/3199), after #2042).
- **Typed holes,** as in Agda, Idris and Hazel: an expression left as a hole is reported with the type expected there
  and the names in scope that fit, and in a debug build it is a deferred error, so the program still runs up to it
  ([#3200](https://github.com/nanyx-lang/nanyx/issues/3200)).
- **Watch expressions,** as in Unison: a line marked to be evaluated on save, its result shown beside it and cached by
  the code it depends on. It would be the editor's way to evaluate an expression, before the debugger has one.
- **Views of a value of one's own,** as in Glamorous Toolkit: a context through which a type says how the debugger and
  live mode show it, as a table, a tree or a chart, as `ToString` says how it is written.
- **Fix and continue,** as in Smalltalk: edit a function while stopped in it and go on. With a recording, it could be
  replaying the run up to the same step through the new code, instead of changing the running program.

# Plan

| Step | Issue | After |
|---|---|---|
| The debug adapter: breakpoints, stepping and variables in VS Code | [#2041](https://github.com/nanyx-lang/nanyx/issues/2041) | |
| Recording a run's effects and replaying it (Planning) | [#2042](https://github.com/nanyx-lang/nanyx/issues/2042) | |
| Stepping backwards (Planning) | [#2043](https://github.com/nanyx-lang/nanyx/issues/2043) | #2041, #2042 |
| `dbg` output kept with each test by the test runner (Planning) | [#2103](https://github.com/nanyx-lang/nanyx/issues/2103) | `nanyx test` |
| `dbg` values inline in the editor: built ([dbg values](../standard-tools/vs-code-extension#dbg-values)) | [#2054](https://github.com/nanyx-lang/nanyx/issues/2054) | |
| Live mode: re-run on save when the entry needs only `Console`, `Async` and `FileSystem`: built ([Live mode](../standard-tools/vs-code-extension#live-mode)) | [#2055](https://github.com/nanyx-lang/nanyx/issues/2055) | #2054 |
| Live mode: a toggle between `dbg` values only and every line's value | [#3198](https://github.com/nanyx-lang/nanyx/issues/3198) | |
| Traces of a function's calls, shown and replayed in the editor (Planning) | [#3199](https://github.com/nanyx-lang/nanyx/issues/3199) | #2042 |
| Typed holes (Planning) | [#3200](https://github.com/nanyx-lang/nanyx/issues/3200) | |
