---
title: "Crash stacks"
description: "How a crash, a deferred error or a runtime error names the file and line of each frame"
order: 15
---

When a program crashes, reaches an [error deferred to run time](../tour/error-handling#deferred-errors), or stops at a
runtime error such as division by zero, `nanyx run` prints the stack under the message, innermost first:

```text
crash: too big: 5
  crash (prelude.nyx:18)
  inner (main.nyx:2)
  middle (main.nyx:6)
  outer (main.nyx:12)
  <top level> (main.nyx:15)
```

Each frame is a function, by the name its source gives it, with the file and line of the code it had reached. A lambda
is `<lambda>` and a script's top-level code `<top level>`; the functions the compiler makes, such as the runtime helpers
and the start function that runs the host, are left out. Files are named by their path in their package, or for a
script by its name, as [`dbg`](../stdlib/console#dbg) names them. Debug and release builds both keep what the stack
needs, so a release build's crash prints the same stack. Planned next: the debugger, on
[Debugging](../work-in-progress/debugging).

# Places in the core IR

Every core IR expression has a `Place`, the file, line and column of the source it was lowered from (`CoreIr.fs`). The lowering
sets `Places.Current` as it lowers each source expression, in `FunctionLowering.Complex`, and restores it afterwards, so
`Expr.make` gives what is made from an expression its place without each call naming it. What the passes after the
lowering make, such as reference counts, has none. A place is not part of what an expression is: every place equals
every other, so no pass that compares expressions sees a difference.

When `CoreIrInline` inlines a call, each place in the callee's body records the callee's name and the place of the
call (`Place.InlinedAt`), and code with no place of its own takes the call's. So inlined code keeps the line it was
written on, as [inlining](../meta/compiler-optimisations#inlining) promises, and the stack still shows the frame
each inlined call would have had: the stack of a program is the same inlined or not.

# Markers and the table of places

With `WasmOptions.Places`, which the command line turns on for every program it writes or runs, `CoreIrEmit` writes a
marker before the code of each expression that calls or can trap, a line comment that WebAssembly ignores:

```text
;;@ prelude.nyx:18:3 | crash | main.nyx:2:17 | inner | main.nyx:6:3
```

That is the expression's file, line and column, then for each call it was inlined through, the inlined function's name
and the call's file, line and column. After the code of an operand computed in place, with markers of its own, the marker of the
expression it is an operand of is written again, so the instruction that follows is not taken for the operand's. A
marker that repeats the one before it is left out.

`WasmBinary`, the compiler's own assembler, reads each marker (`WatSyntax.Placed`) and notes where in the module the
code after it starts. It writes them into a custom section, `nanyx.spans` (`WasmBinary.spanTableName` says how it is
laid out): the names of files and functions, the frames, each a function's name, a file, a line, a column (0 for none)
and the frame of the call it was inlined at, so that inlined code shares its outer frames, each stretch of code by how
far after the last it starts, the probes of a build with them (see [Debug probes](./debug-probes)), and each function's
definition, by its name: the frame of the marker `;;@def main.nyx:3` that starts its body, which has no column and a
profile gives the function (see [Profiling](./command-line#profiling)). The function names the frames fall back on are
in the name section. A frame is kept once for each place, so two places on one line at different columns are two frames:
the columns made the table about 38% larger across the examples, and their modules about 7% larger
([#2950](https://github.com/nanyx-lang/nanyx/issues/2950)). A stack prints each frame's file and line; a crash's report
names the column too.

# The runtime

`nanyx run` runs the module as `WasmBinary` assembled it, so that Wasmtime's frames are at the offsets the table
gives. A crash, or a deferred error, writes its message to stderr and then traps in `__nanyx_crashed` (the prelude's
`crash` calls the `__nanyx_crashed` intrinsic with its message, and a deferred error's code calls the helper itself
with its messages as one), rather than exiting, so Wasmtime keeps the frames. `WasmRunner.trapped` reads the table
(`SpanTable`), finds each frame's place by its module offset, expands a frame of inlined code into a frame for each
call it was inlined through, and gives `RunError.Crashed` with the stack, or the stack with the trap's own error. The
command line prints it under the message and exits with 1, and a served program's request that fails reports it with
the 500.

## The crash report

`RunError.Crashed` is a report as data ([#2933](https://github.com/nanyx-lang/nanyx/issues/2933)), for whatever runs
the program to act on without reading it back out of stderr: the message, the place, and the stack.

- **The message** reaches the runtime through an import. With `WasmOptions.CrashToRuntime`, `__nanyx_crashed` calls
  `nanyx:crash`'s `report` with the address and length of the message's bytes before it traps (`CodeGen.CrashReport`),
  and the runtime keeps it (`CrashImport`), reading it through the caller's memory, so a crash in the start function,
  while the module is being instantiated, reports it too. The command line sets the option for what it runs itself:
  `nanyx run`, and a served program's instances (`RunProgram`). A module `nanyx build` or `nanyx wasm` writes, a
  component, and the playground's have no such import, and the report's message is then none. Every module still
  writes the message to stderr itself, so what a crash prints is the same with the option or without. An error deferred
  to run time calls `nanyx:crash`'s `deferred` first, through `__nanyx_deferred`, so the runtime knows the crash for one
  the compiler found rather than one the program made, which a test that expects a crash can't pass by
  ([Test builds](./test-builds#tests-that-expect-a-crash)).
- **The place** is the file, line and column of the program's own code it crashed in: the innermost frame of the stack outside
  the standard library. So a `crash` or an `orCrash` is placed at the line that called it, not in the prelude. The
  table knows which files are the library's: the code generator writes a `;;@lib prelude.nyx` marker among the
  module's fields for each library file a marker names (`CoreIrEmit.libraryMarker`, for a place whose file is a
  library module's, `nanyx/prelude.nyx`), and `WasmBinary` writes their names' numbers as the table's last vector. A
  place's column is the frame's: for a `crash` or an `orCrash`, where the program called it, for an `assert`, where it
  is written, and for a deferred error, where the expression it replaced starts, as its message says
  ([#2950](https://github.com/nanyx-lang/nanyx/issues/2950)). `nanyx test` writes the place first in a crashed test's
  report, before what it printed, unless that starts with the place already, as an `assert`'s message does.

A served program's 500 is logged with the place and the message: `it crashed at src/main.nyx:5:45: no such page`, then the
stack.
