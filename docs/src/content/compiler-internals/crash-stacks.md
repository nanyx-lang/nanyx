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

Every core IR expression has a `Place`, the file and line of the source it was lowered from (`CoreIr.fs`). The lowering
sets `Places.Current` as it lowers each source expression, in `FunctionLowering.Complex`, and restores it afterwards, so
`Expr.make` gives what is made from an expression its place without each call naming it. What the passes after the
lowering make, such as reference counts, has none. A place is not part of what an expression is: every place equals
every other, so no pass that compares expressions sees a difference.

When `CoreIrInline` inlines a call, each place in the callee's body records the callee's name and the place of the
call (`Place.InlinedAt`), and code with no place of its own takes the call's. So inlined code keeps the line it was
written on, as [inlining](../foundations/compiler-optimisations#inlining) promises, and the stack still shows the frame
each inlined call would have had: the stack of a program is the same inlined or not.

# Markers and the table of places

With `WasmOptions.Places`, which the command line turns on for every program it writes or runs, `CoreIrEmit` writes a
marker before the code of each expression that calls or can trap, a line comment that WebAssembly ignores:

```text
;;@ prelude.nyx:18 | crash | main.nyx:2 | inner | main.nyx:6
```

That is the expression's file and line, then for each call it was inlined through, the inlined function's name and the
call's file and line. After the code of an operand computed in place, with markers of its own, the marker of the
expression it is an operand of is written again, so the instruction that follows is not taken for the operand's. A
marker that repeats the one before it is left out.

`WasmBinary`, the compiler's own assembler, reads each marker (`WatSyntax.Placed`) and notes where in the module the
code after it starts. It writes them into a custom section, `nanyx.spans` (`WasmBinary.spanTableName` says how it is
laid out): the names of files and functions, the frames, each with the frame of the call it was inlined at so that
inlined code shares its outer frames, each stretch of code by how far after the last it starts, a debug build's probes
(see [Debug probes](./debug-probes)), and each function's definition, by its name: the frame of the marker `;;@def
main.nyx:3` that starts its body, which a profile gives the function (see [Profiling](./command-line#profiling)). The
function names the frames fall back on are in the name section. Across the examples, the table adds 16% to a release build's module.

# The runtime

`nanyx run` runs the module as `WasmBinary` assembled it, so that Wasmtime's frames are at the offsets the table
gives. A crash, or a deferred error, writes its message to stderr and then traps in `__nanyx_crashed` (the prelude's
`crash` calls the `__nanyx_crashed` intrinsic, and a deferred error's code calls the helper itself), rather than
exiting, so Wasmtime keeps the frames. `WasmRunner.trapped` reads the table (`SpanTable`), finds each frame's place by
its module offset, expands a frame of inlined code into a frame for each call it was inlined through, and gives
`RunError.Crashed` with the stack, or the stack with the trap's own error. The command line prints it under the
message and exits with 1, and a served program's request that fails reports it with the 500.
