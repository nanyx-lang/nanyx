---
title: "Compilation"
description: "What the backend compiles, how constraints are solved and errors deferred, the binary format, and how the docs examples are tested"
order: 1
---

This section says how the compiler and its tools work today. What is still open, undecided or planned is in
[Work in progress](../work-in-progress/workstreams), where each workstream has a page. This page covers what the
WebAssembly backend compiles, how the type checker solves constraints and defers errors, the binary format the backend
writes, and how the docs' examples are tested; the compilation
workstream's own page is [Compilation](../work-in-progress/compilation).

# What the backend compiles

The parser, type checker, WebAssembly backend and runtime are libraries; the `nanyx` CLI is the only executable
(see [The `nanyx` command](../standard-tools/command-line)). The backend lowers a typed module to the core IR (see
[Memory management](./memory-management#core-ir)), writes WebAssembly text from it, and `WasmBinary.fs` assembles the
text to the binary format for `nanyx build` and `nanyx wasm` (below).

- Top-level `def` values are exported as wasm functions, except one that declares contexts (see
  [Context calls](./context-calls)). Top-level expressions (and the `@entry` definition) run from a WebAssembly start
  function. A module with a start function emits only the functions the start reaches, through calls and closures,
  and binds no static closure that nothing reads (`CoreIrReach.fs`); so does a library component, from its exports;
  any other module without one emits every definition.
  With `WasmOptions.ExportStart` the start function is exported as `__nanyx_start` instead of run when the module is
  instantiated, so that an embedder such as a page can reach the module's memory first and call it itself
  ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)); `WasmOptions.CheckMemory` exports it too.
- `dbg(...)` of a value of any type writes the value through a printer function generated for its type
  (`CoreIrLowerDbg.fs`), after where it was called: the line, and the file's path in its package or a script's name
  (`WasmOptions.SourceRoot`). A release build (`WasmOptions.Release`) lowers `dbg(x)` to `x`. The host decides where
  it goes: a module the runtime runs gives each `dbg` to `nanyx:debug` `dbg` first, with the file, line, column and
  text, and writes it to stderr itself, through WASI `fd_write` (`$dbg_str`), when the runtime leaves it to the program,
  as the console runner does; a module written to a file, which another runtime may run, has no such import and always
  writes it itself (`EmitContext.DbgToRuntime`, `Probing.defineDbg`). `WasmOptions.DbgToEmbedder` gives the import
  without the probes, for an embedder such as a page that keeps `dbg` apart from what the program writes.
- How values are laid out and freed is in [Memory management](./memory-management), and how context members are
  passed in [Context calls](./context-calls).

Every compiler error has a code and a title from one table, `DiagnosticCodes` in `Diagnostics.fs`, and the span of
the node it is about; [Compiler errors](../standard-tools/errors) lists them. A construct the backend cannot lower yet
is reported as an `UnsupportedByBackend` error (`NYX0301`) where it is
written (see [Memory management](./memory-management#core-ir)). A broken invariant of the backend, such as a context
member the type checker found but the backend cannot, is a bug in the compiler, not a missing feature. It is reported
as a `BackendInternalError` (`NYX0302`) that asks for a bug report. The backend raises one with
`CoreIr.internalError`, never a bare `failwith`.

# Solving constraints

The type checker holds one list of constraints for the whole module, adding new ones at its front, and solves it again
as it goes (`Solving.unifyDetailed`). A list that ends in one solved lately, and that agreed, is solved on top of what
that one solved to: only the constraints in front of it are solved. Once it has generalised a top-level def, the type
checker marks the list it holds as a checkpoint (`Solving.checkpoint`); a later list whose newer constraints name none
of the checkpoint's variables is solved on top of it, since the two share nothing and the order they are solved in
can't show. Otherwise the whole list is solved afresh, newest first, so that the failure reported, and the order in
which a union's tags are added, are what solving it afresh gives.

Solved on top of an earlier solution, a variable can stand for another that stands for another in turn: an else-if
joins each branch to the next, so each branch's variable stands for the one before it. `Unifier.unifyFrom` follows such
a chain of the earlier solution once and remembers where it ends for each variable on it, so a long else-if checks in
time that grows with its length. A type read against a solution once it is found, such as the type of each node of the
module, is resolved with `Unifier.applySolved`, which keeps what each variable resolves to with the solution.

# Deferred errors

What deferred errors are for users is in [Deferred errors](../tour/error-handling#deferred-errors). The approach is the
one GHC takes with `-fdefer-type-errors`, and a pragmatic cousin of Hazel's marked holes: the error becomes a node in
the typed program, with a type that fits anywhere (`???`, `Unifier.errorType`), and evaluating it fails. The deferral
pass is `DeferredErrors.deferErrors`, and the backend lowers what it leaves to a `crash` (see
[Memory management](./memory-management#core-ir)).

- **A failed call keeps its callee's declared result.** A call that fails for a reason outside its callee's type (a
  missing context, a callee a module block doesn't export, an argument passed by position for a named parameter) is
  recorded as failed (`FailedCalls`) and checked on with the callee's type. One given an argument of the wrong type, or
  the wrong number of them, is checked against the callee's type as any call is. Only what a failed argument would have
  decided is `???`: `list.map(xs, 5)` is a `list(???)`. A call whose callee is unknown, ambiguous or a context instance
  is `???` whole.
- **Conflicts among constraints.** Once the module is checked, its constraints are solved past each failure: the
  constraints from where it failed are set aside and the rest solved again, until they agree. The variables of a
  failed constraint's expected side that nothing else fixes are bound to the error type, solved after everything else
  (`Solving.solveLeavingUnknown`). The expressions where a conflict was found are otherwise typed by their other
  constraints, and the deferral pass replaces the outermost expression at the conflict's span.

# The binary format

`nanyx build` and `nanyx wasm` write `.wasm`; `--wat` writes the text instead, and `nanyx run` runs the text on
Wasmtime. `WasmBinary.assemble` is an assembler of
the compiler's own, so a browser build of the compiler, which has no Wasmtime, writes `.wasm` too. `WatSyntax.fs` reads
the text as s-expressions, and `WasmBinary.fs` numbers the module's definitions (imports first in each index space,
the types a type use or a block writes inline added after the declared ones) and writes the sections. It reads what the
backend writes, flat or folded: WebAssembly 2.0's numeric, variable, memory and control instructions, with
`return_call` and `return_call_indirect`, and no passive segments, SIMD or reference instructions. Functions keep their
names in the `name` section, so a trap's backtrace, and the runner's out-of-memory check, see them as they do for the
text. A form it doesn't read is a `WasmAssemblyError`, which the CLI reports as a compiler bug.
`WatSyntax.read` keeps the lists it is in on a stack of its own, and `assemble` runs on a large stack
(`Nanyx.LargeStack.run`), as writing a folded instruction recurses once per level it nests: an `else if` chain of
thousands of branches nests its `if`s thousands deep.

The tests run programs that print, trap, make a million tail calls, and use closures, records and tags from the binary
and the text and compare what they do (`Features/Binary/WasmBinaryTests.fs`), assemble every end-to-end test's module
and validate it, and run the memory-checked examples from both. Every module a test compiles with `transpileWatWith` is
assembled too.

# Tour examples

Every ` ```nanyx ` block in the [language tour](../tour/variables), the [Getting started](../getting-started/basics)
pages, the [Standard Library](../stdlib/standard-library) pages, the [Custom patterns](../advanced/custom-patterns)
page and the [JSON cookbook](../cookbook/json-parsing) page is a test case: `RunTourExampleTests.fs` in
`Nanyx.Compiler.Wasm.Tests` reads their pages, and compiles and runs each block in the test process, as `nanyx run`
would run it as a script. Packages from the index that a block names, such as `::dependency nanyx.json 0.1.0`, are read
from an index on disk that the tests make (`testStore` in `TestHelpers.fs`), from the `.nyxpkg` files of their releases
in `Nanyx.Compiler.Wasm.Tests/Packages`, so no example needs the network. A standard-library page's generated reference is left out (see
[Standard library implementation](./standard-library-implementation#its-pages-on-the-docs-site)). The words after
`nanyx` on a block's opening fence say what kind of example it is:

| Fence | What the test does |
|-------|--------------------|
| ` ```nanyx ` | The block is a whole program. It must compile with no errors, and run with exit code 0 and nothing on standard error. |
| ` ```nanyx fragment ` | The block is part of a program: it uses names defined elsewhere, stands for code with `...`, or is one file of several. It is not compiled. |
| ` ```nanyx skip=#123 ` | The block is a program the compiler gets wrong, until issue #123 is fixed. It is not compiled. When the issue is fixed, remove the tag. |

A block fenced ` ```nanyx ` must also be formatted, here and on every other docs page: a formatter test checks each one
that parses and has no `-- error` comment ([Formatter](./formatter#the-repositorys-nanyx)). Outside the tour, ` ```nanyx unformatted `
marks code shown as it was before formatting, which that test leaves alone.

A whole program is checked further by what its comments say:

- **Errors.** A block with a comment that names an error, such as `-- error NYX0201: ...` or `-- Error NYX0227: ...`,
  shows that error. It is compiled and not run, and the errors it reports must have exactly the codes its comments
  name.
- **Output.** A block that prints something and ends with comment lines prints those lines, without their `--`.
  Comments that give a value, such as `def result = add5(10)  -- 15`, are not checked.

The tour's examples print with the console host's prelude, which every program sees, and use strings without
importing `nanyx/text`, so the test adds that import to every block that doesn't have it, after its module's doc comment
and declaration if it has them, but for a block that turns a linter on with `::linter`, which would report the import
unused. Anything else a block uses, such as `nanyx/math`, it imports itself.

The examples on the docs site's home page are files of their own, `docs/src/home/*.nyx`, which the page reads and
highlights as the docs' code blocks are highlighted. The same test runs each as it is, with no import added, since the
page's **Run in the playground** link opens it so, and checks it prints what its last comments say.
