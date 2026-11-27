---
title: "Debug probes"
description: "How a debug build tells the runtime where it is, keeps a shadow stack, and writes a frame's locals"
order: 16
---

A program built with debug probes calls the runtime at the start of each function, at each statement and at each
place a function ends, and the runtime keeps a shadow stack from them and can ask for the locals of the frame it is in.
They are the foundation of the debugger, which is planned on [Debugging](../work-in-progress/debugging). Only code that
listens to them asks for them (`WasmOptions.Probes`): the debugger will, when it attaches to a run, and the tests do.
Nothing else listens, so no command builds with them: `nanyx run` and `nanyx test` compile without them, as a release
build does, and so do `nanyx build` and `nanyx wasm`, whose module another WebAssembly runtime, without the imports
below, may run. The probes made most of a program's code: `examples/shop.nyx`, compiled as `nanyx run` compiles it, is
2.69 MB of WebAssembly text with them and 0.93 MB without
([#2225](https://github.com/nanyx-lang/nanyx/issues/2225)).

# Probes in the core IR

A probe is a core IR expression, `Probe(kind, locals)` (`CoreIr.fs`), of unit type, at a place (see
[Crash stacks](./crash-stacks#places-in-the-core-ir)). The lowering puts one before each statement of a block, written
to write the locals in scope there: the function's parameters, and the 64 locals bound last (`CoreIrProbes.probeLimit`,
kept in order in the scope as each `def` binds one). Only a function with more than 64 locals in scope at once, which is
code a program generates, shows fewer than all of them; writing them all made a function of n locals n² in size, and one
of 8,000 ran the compiler out of memory ([#2488](https://github.com/nanyx-lang/nanyx/issues/2488)). It wraps each function the source writes, a lifted lambda among them, in
`CoreIrProbes.around`: an entry probe, which names the function and writes its parameters, and a return probe at each
place it ends. A function that ends in a call ends before the call, so that a tail call stays one, and the callee's
entry follows; one that ends in another value ends once it has it, in a local. A local is written only if a `dbg`
printer can write its type (`CoreIrLowerDbg.printable`), and not a cell's, whose local holds its box.

A probe uses none of its locals, so reference counting never keeps a value alive for one, and a debug build frees and
reuses memory as a release build does. The inliner doesn't count probes in a function's size, so debug and release
builds inline alike, and a probe inlined with its function keeps the place it was written at, with the calls it was
inlined through.

# The code

The emitter writes a probe as a check of whether the runtime is probing: the start function sets a global,
`$__nanyx_probing`, from the runtime's `nanyx:debug` `enabled` before anything else runs, and each function with probes
copies it into a local when it starts, which can stay in a register. When it is set, the probe calls `nanyx:debug` `probe` with its
number; if that gives 1, it writes each of its locals that is still alive there through its printer, and passes the
local's name and text to `nanyx:debug` `local`. A local is still alive where the rest of the code reads it and doesn't
bind it again (`CoreIrEmitReads.ChainReads`, worked out once for each chain): one never read again may have been given up
already. A probe that a printer
takes its argument over from first gives it a reference of its own.

Before the probe's code go the marker of its place and its own, `;;@probe 3 statement` or `;;@probe 1 entry count`.
`WasmBinary` writes each probe into the table of places, `nanyx.spans`, by its number: its kind, its function's name for
an entry, and its place, so the runtime knows each probe without the program telling it more than a number.

# The runtime

`Probing.define` answers the imports. Without a listener, `enabled` gives 0 and the program never calls the runtime;
with one (`WasmRunner.runListening`, an `IProbeListener`), each probe the program passes is given to it by its number in
the table, and it says whether it wants the locals. `ShadowStack` keeps the frames from the probes, innermost first,
each at the place of the last probe it passed: an entry pushes a frame, a statement moves the top one, and a return pops
it. A function inlined into another has a frame of its own, since its probes were inlined with it.

# `dbg`

A module with probes gives what each `dbg` prints to the runtime too: `nanyx:debug` `dbg(file, line, column, text)`,
the file's name and the text as strings, says whether the runtime has dealt with it, and when it hasn't the program
writes `[file:line] text` to its stderr itself, as a module without the import does (`EmitContext.DbgToRuntime`). The
runtime's handler is the host's (`Probing.defineDbg`): `WasmRunner` leaves it to the program, so that it keeps its place
among what else the program writes to stderr, and `WasmServer` writes it to the server's log with the request it came
from ([Hosts](../advanced/hosts#where-dbg-prints)). A module compiled with `WasmOptions.DbgToEmbedder` imports
`nanyx:debug` `dbg` in the same way but none of the probes' imports, for an embedder that only wants `dbg`, such as a
page.

# What it costs

Without a listener, a probe is a read of a local and a branch. Against the same program built without probes, with the
module's size and the fastest of 21 runs:

| Program | Without | Without a listener | Listening | Reading locals at every statement |
|---|---|---|---|---|
| `fib(30)`, a call for each node | 2.3 KB, 9.9 ms | 7.0 KB, 13.4 ms (+35%) | ×62 | ×60 |
| A tail-recursive loop of 30 million | 2.4 KB, 61 ms | 7.2 KB, 75 ms (+22%) | | |
| `map`, `filter` and `fold` over 300,000 | 4.6 KB, 13.1 ms | 11.2 KB, 16.4 ms (+25%) | | |

Reading the global at each probe rather than a local cost about twice as much (+42% for the loop, +57% for the lists).

Reading every frame's locals at every statement costs little more than listening: it is the call to the runtime at each
probe that costs, so a debugger listens only while it is stepping or has a breakpoint, and asks for locals where it
stops.

The design call, how a frame's locals stay reachable: they are written by the program when the runtime asks, at the
probe, rather than spilled to a shadow frame in linear memory at each probe. Spilling would cost stores at every probe
while a debugger listens, and would leave the runtime with references that reference counting may already have given
up, since a local is dropped after its last use, not at the end of its scope; keeping them alive would change when
memory is freed, and stop in-place reuse, in every debug run.
