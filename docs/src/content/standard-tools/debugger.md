---
title: "Debugger"
description: "Stopping a program at breakpoints, stepping it, and seeing its stack and variables, through the Debug Adapter Protocol"
order: 6
---

`nanyx run --debug` runs a program under the debugger. It builds the program with
[debug probes](../deep-dives/debug-probes), so that it can stop at any statement, and speaks the
[Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/) on its standard input and output. An
editor that speaks it, such as VS Code, is the other end: it sets breakpoints, steps the program and shows its stack and
variables. The command takes what `nanyx run` takes, a script or a package, and the program's arguments after it:

```text
nanyx run --debug tool.nyx -- --verbose
```

What the program writes to its standard output and standard error goes to the editor, which shows it as the program
runs, in VS Code's Debug Console. Its standard input is empty, so `readLine` sees the end of input at once. It can't be
given with `--profile`, `--check-memory`, `--memory` or `--dbg-to`, and a program that is served, such as an HTTP
host's, can't be debugged yet.

# Breakpoints and stepping

- **A breakpoint** is on a line. It stops the program at a statement that starts on that line, before the statement
  runs. One on a line with no statement, such as a blank line or a `def` of a function, moves to the next line that has
  one, and the editor shows it there. One past the last statement of its file has none, and the editor shows it greyed.
- **Continue** runs to the next breakpoint.
- **Step over** runs to the next statement of the function the program stopped in. It runs through the functions it
  calls, and when the function returns, it stops in the function that called it.
- **Step into** runs to the next statement, in a function the current one calls too.
- **Step out** runs to the next statement of the function that called the current one.
- **Pause** stops a running program at the next statement it reaches, or at the next function it enters.
- **Stop on entry**, the launch configuration's `stopOnEntry`, stops the program at its first statement.

A function inlined into another is stepped into and out of as if it were called, since its probes were inlined with it.
A function that ends in a call to another ends before it, so that the call is a tail call. Stepping out of the callee
goes back past it, to the function that called both.

# The stack and variables

The stack is the functions the program is in, innermost first, each with its file and line. A function is named as the
program writes it, as a [crash's stack](../deep-dives/crash-stacks) names it. A file of the standard library's is shown
by its name, without a file to open, and the functions the compiler makes, such as the host's start, are left out.

A frame's variables are its locals: its parameters, and the locals it has bound so far that are still alive. Each is
written as [`dbg`](../stdlib/console#dbg) writes it. A record expands into its fields, a tuple into its parts, a list, a
set or an array into its elements, a map into its entries by key, a tag into its payload's parts, and a type of your own
into its shape's. A function's value says it is one, `<function int -> int>`, and a value no printer writes says its
type, `<value of MArray(int)>`.

The debugger asks for a frame's locals only where the program stops, since writing every frame's at every statement
would slow the program down without bound when a local is large
([#3253](https://github.com/nanyx-lang/nanyx/issues/3253)). So the frame the program stopped in shows its locals as they
are. A frame further out shows them as they were the last time the program stopped in it, under **Locals at line N**.
If the program never stopped in that frame, it says its locals weren't recorded: set a breakpoint in it to see them.

# Crashes

A crash, an `assert` that fails, or an error the compiler deferred to run time stops the program where it crashed,
before it ends, with the crash's message. The stack shows the frame that crashed, and the frames that called it. A
function whose last call never returns, such as a call of `crash`, keeps its frame for it, which a tail call wouldn't.
The frame's locals are those of the last time the program stopped in it, as for a frame further out
([#3257](https://github.com/nanyx-lang/nanyx/issues/3257)). Continuing ends
the program, as `nanyx run` ends it: the crash's stack goes to the Debug Console, and the program's exit code is 1.

How the runtime keeps the stack, answers the probes and stops is described in
[Debug probes](../deep-dives/debug-probes#the-debugger).
