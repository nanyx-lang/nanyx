---
title: "Debug probes"
description: "How a debug build tells the runtime where it is, keeps a shadow stack, and writes a frame's locals"
order: 16
---

A program built with debug probes calls the runtime at the start of each function, at each statement and at each
place a function ends, and the runtime keeps a shadow stack from them and can ask for the locals of the frame it is in.
They are the foundation of the [debugger](../standard-tools/debugger), `nanyx run --debug`. Only code that listens to
them asks for them (`WasmOptions.Probes`): the debugger does, and the tests do.
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
entry follows, but for a call that never returns, one of a function whose result is `undefined`, as `crash`'s is
(`CoreIrProbes.endsTheProgram`), which keeps the frame, so that the debugger shows the frame that crashed; one that ends
in another value ends once it has it, in a local. Such a call, wherever it is, has a probe of its own just before it,
a crashing probe (`CoreIrProbes.beforeCrash`, from `CoreIrLower`'s `CallDefinition`), which writes the locals in scope
there, as a statement's does. A
local is written only if a `dbg`
printer can write its type (`CoreIrLowerDbg.printable`), and not a cell's, whose local holds its box.

A probe uses none of its locals, so reference counting never keeps a value alive for one, and a debug build frees and
reuses memory as a release build does. The crashing probe is the exception: nothing after it reads anything, so every
local but the call's arguments would be dead there, and given up. It borrows the locals it writes
(`CoreIrCounting.operands`), which keeps them alive to it; the program ends there, so that costs nothing on the way
out. But it writes only those whose keeping changes nothing before it (`CoreIrCounting`'s `crashingProbesTrimmed`): not
a value on the heap the code took over on the way there, by an owning use, such as passing a list to `list.map`, or as
a value a `match` takes apart, either of which may reuse its memory in place. Keeping such a value would copy where the
code reused, on every run of a debug build, not only one that crashes: a loop that maps a list it owns before a check
that calls `crash` copied the list each time round, 51 times the allocations and 2.8 times the work for 50 maps of
1,000 elements. The inliner doesn't count probes in a function's size, so debug and release
builds inline alike, and a probe inlined with its function keeps the place it was written at, with the calls it was
inlined through.

# The code

The emitter writes a probe as a check of whether the runtime is probing: the start function sets a global,
`$__nanyx_probing`, from the runtime's `nanyx:debug` `enabled` before anything else runs, and each function with probes
copies it into a local when it starts, which can stay in a register. When it is set, the probe calls `nanyx:debug` `probe` with its
number; if that gives 1, it writes each of its locals that is still alive there through its printer, and passes the
local's name and text to `nanyx:debug` `local`, and then calls `nanyx:debug` `written`, so that the runtime knows they
are all written and the statement hasn't run: where the debugger stops. A local is still alive where the rest of the code reads it and doesn't
bind it again (`CoreIrEmitReads.ChainReads`, worked out once for each chain): one never read again may have been given up
already. A probe that a printer
takes its argument over from first gives it a reference of its own.

A crashing probe writes its locals whatever the rest of the code reads, since it reads them itself.

Before the probe's code go the marker of its place and its own, `;;@probe 3 statement` or `;;@probe 1 entry count`.
`WasmBinary` writes each probe into the table of places, `nanyx.spans`, by its number: its kind, its function's name for
an entry, and its place, so the runtime knows each probe without the program telling it more than a number.

# The runtime

`Probing.define` answers the imports. Without a listener, `enabled` gives 0 and the program never calls the runtime;
with one (`WasmRunner.runListening`, an `IProbeListener`), each probe the program passes is given to it by its number in
the table, and it says whether it wants the locals. `ShadowStack` keeps the frames from the probes, innermost first,
each at the place of the last probe it passed: an entry pushes a frame, a statement or a crashing probe moves the top
one, and a return pops it. A function inlined into another has a frame of its own, since its probes were inlined with it. A crash's `nanyx:crash`
`report` tells the listener too (`IProbeListener.Crashed`, through `CrashImport.defineWith`), before the trap, while the
frames are still on the stack.

# The debugger

`nanyx run --debug` (`DebugCommand.cs`) compiles the program with probes and runs it under `DebugServer`, a Debug
Adapter Protocol server on its standard input and output (`Dap` frames the messages). The program runs on a thread of
its own, and what it writes goes to files (`ProgramIo.ToFiles`) that the server sends on as `output` events as they
grow. The server is its listener. It keeps the shadow stack and decides at each probe whether to stop, by
`DebugSession`, which knows the breakpoints and how the program goes on and has no other state:

- **A breakpoint** is a line of a file, as the table of places names it, moved to the first line at or after it with a
  statement probe (`SpanTable.statementLines`).
- **Steps** are measured by the shadow stack's depth. A step in stops at the next statement. A step over stops at the
  next statement at the same depth or shallower, and a step out at the next one shallower. A pause stops at the next
  statement, or the next function's entry, since a loop that calls itself may have no statement. The client's request
  only sets a flag, which the program's thread takes into the session at its next probe: the session is shared under a
  lock that a running loop takes at every probe, and a lock isn't fair, so a request that waited for it could wait as
  long as the loop runs.
- **The place it stopped at** isn't stopped at again until another probe has passed, so that resuming at a breakpoint
  goes on.

To stop, the probe answers 1, the program writes the frame's locals, and its `written` blocks the program's thread
until the client resumes it. The server keeps each frame's locals by its depth, dropping a depth's when a frame is
entered or left there, so a frame further out has the locals of the last time it stopped in it. A crashing probe always
asks, without stopping, and keeps what it writes as its frame's: it runs once, on the way into a crash, so a run that
doesn't crash never pays for it. The crash then shows the frame that called the function that never returns with those
locals, **Locals at line N**. It doesn't ask
anywhere else: asking at every statement printed 286 million characters for a recursion over a list of 5,000, against
none ([#3253](https://github.com/nanyx-lang/nanyx/issues/3253)). A crash's `report` stops it with reason `exception`.
The Variables view gets each local's text read back into a tree (`ValueTree`), whose parts are references the client
expands, numbered afresh at each stop.

# `dbg`

A module with probes gives what each `dbg` prints to the runtime too: `nanyx:debug` `dbg(file, line, column, text)`,
the file's name and the text as strings, says whether the runtime has dealt with it, and when it hasn't the program
writes `[file:line] text` to its stderr itself, as a module without the import does (`EmitContext.DbgToRuntime`). The
runtime's handler is the host's (`Probing.defineDbg`): `WasmRunner` leaves it to the program, so that it keeps its place
among what else the program writes to stderr, and `WasmServer` writes it to the server's log with the request it came
from ([Hosts](../advanced/hosts#where-dbg-prints)). A module compiled with `WasmOptions.DbgToEmbedder` imports
`nanyx:debug` `dbg` in the same way but none of the probes' imports, for an embedder that only wants `dbg`, such as a
page.

`nanyx run --show-values` builds a program that writes every value its own code binds or gives as if it were a `dbg`
(`WasmOptions.ShowValuesIn`, the files whose code it shows). Before it is lowered, `ShownValues.wrap` puts a call of
`__show`, a name no code can write, around each such value: a local `def`'s that isn't a function, at the `def`; a
top-level one's, at its value, since a top-level item comes back from the type checker without a span of its own; each
statement whose value isn't `()`, a block's last among them; and a lambda's body that isn't a block. It leaves out a
written `dbg`, whatever the compiler made up, a node with no file and a definition whose name starts `__`, such as a
derived instance's, written at the code that needed it, and the script's own entry point, `__script`, is the one such
definition it goes into. `__show` is lowered as `dbg` is, but calls `nanyx:debug` `value` in place of `dbg`, with the
same arguments, and its printer, `ShowingAny`, writes a value as `dbg`'s does only where `CoreIrLowerDbg.printable`
says it can, and otherwise by its type, `<value of MArray(int)>`, so that showing a value never makes a program fail
to compile. A sequence is always written by its type, `<sequence of int>`, and never pulled. The runtime's `value` is
`Probing.defineDbg`'s handler too, with the hit marked `Shown`, and the channel `--dbg-to` writes says so in each line's
`kind`. A program built without it is unchanged: it imports `value` only when it shows a value.

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
