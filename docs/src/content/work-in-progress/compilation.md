---
title: "Compilation"
description: "The parser, type checker, backend, runtime and CLI: where they are and what comes next"
order: 21
---

This page frames the compilation workstream: what it covers, where the compiler stands and what comes next. Work is
tracked under the `compilation` label.

# Summary

The compilation workstream is everything between a `.nyx` file and a running program: the parser, the type
checker, the WebAssembly backend, the runtime that hosts the output on Wasmtime, and the `nanyx` CLI. Its measure
of progress is simple: a program the documentation describes should parse, type-check and run.

# Scope

| Area | Project |
|------|---------|
| Parser | `Nanyx.Parser` |
| Type checker | `Nanyx.Compiler` (`Infer.fs`, `Unifier.fs`, `Library.fs`) |
| WebAssembly backend | `Nanyx.Compiler.Wasm` |
| Runtime | `Nanyx.Compiler.Runtime` |
| CLI | `Nanyx.Compiler.Cli` |
| Standard library | `stdlib/nanyx/`, and the intrinsics it needs; checked once at build time by `Nanyx.Compiler.CheckedLibrary` ([Standard library implementation](./standard-library-implementation)) |

Out of scope here, with their own workstreams:

- What diagnostics say and how they are shown: [Error messages](./error-messages).
- Spans, symbols and the semantic model, when they are built for linters: [Linters](./linters). The
  refactors land in this codebase, but the design and tracking live there.
- Editor features: [Language server](./language-server).

# Where the compiler stands

- **Features can reach the three phases unevenly.** A construct may parse before it type-checks, and type-check
  before the backend can generate code for it. Gaps are filed per phase under the `parser`, `type checker` and
  `wasm backend` labels.
- **Type and name errors are deferred.** A debug build runs in spite of them, reporting each if the program reaches
  it; `--release` refuses them. See [Deferred errors](./deferred-errors).
- **A parse error stops everything.** There is no error recovery, so one syntax error hides every problem after
  it. The language server needs recovery too. See [The parser](./parser).
- **Some defs are still solved afresh.** The type checker holds one list of constraints for the
  whole module and solves it again as it goes (`Unifier.unifyDetailed`). A list is solved on top of the last one
  solved, or of the checkpoint the type checker made once it generalised the def before (`Unifier.checkpoint`), when
  that can't change the result. Defs that share a variable (mutual recursion, a use before the def is checked) and a
  module with a type error still solve afresh, newest first.

## What the backend compiles

The parser, type checker, WebAssembly backend and runtime are libraries; the `nanyx` CLI is the only executable
(see [The `nanyx` command](../standard-tools/command-line)). The backend lowers a typed module to the core IR (see
[Memory management](./memory-management#core-ir)), writes WebAssembly text from it, and `WasmBinary.fs` assembles the
text to the binary format for `nanyx build` and `nanyx wasm` (below).

- Top-level `def` values are exported as wasm functions, except one that declares contexts (see
  [Context calls](./context-calls)). Top-level expressions (and the `@entry` definition) run from a WebAssembly start
  function. A module with a start function emits only the functions the start reaches, through calls and closures,
  and binds no static closure that nothing reads (`CoreIrReach.fs`); a module without one emits every definition.
- `dbg(...)` of a value of any type, and the console host's `Console`, write to stdout through WASI `fd_write`; `dbg`
  writes the value through a printer function generated for its type (`CoreIrLowerDbg.fs`).
- How values are laid out and freed is in [Memory management](./memory-management), and how context members are
  passed in [Context calls](./context-calls).

A construct the backend cannot lower yet is reported as an `UnsupportedByBackend` error (`NYX0301`) where it is
written (see [Memory management](./memory-management#core-ir)). A broken invariant of the backend, such as a context
member the type checker found but the backend cannot, is a bug in the compiler, not a missing feature. It is reported
as a `BackendInternalError` (`NYX0302`) that asks for a bug report. The backend raises one with
`CoreIr.internalError`, never a bare `failwith`.

## The binary format

`nanyx build` and `nanyx wasm` write `.wasm`; `--wat` writes the text instead, and `nanyx run` runs the text on
Wasmtime. `WasmBinary.assemble` is an assembler of
the compiler's own, so a browser build of the compiler, which has no Wasmtime, writes `.wasm` too. `WatSyntax.fs` reads
the text as s-expressions, and `WasmBinary.fs` numbers the module's definitions (imports first in each index space,
the types a type use or a block writes inline added after the declared ones) and writes the sections. It reads what the
backend writes, flat or folded: WebAssembly 2.0's numeric, variable, memory and control instructions, with
`return_call` and `return_call_indirect`, and no passive segments, SIMD or reference instructions. Functions keep their
names in the `name` section, so a trap's backtrace, and the runner's out-of-memory check, see them as they do for the
text. A form it doesn't read is a `WasmAssemblyError`, which the CLI reports as a compiler bug.

The tests run programs that print, trap, make a million tail calls, and use closures, records and tags from the binary
and the text and compare what they do (`Features/Binary/WasmBinaryTests.fs`), assemble every end-to-end test's module
and validate it, and run the memory-checked examples from both. Every module a test compiles with `transpileWatWith` is
assembled too.

## Tour examples

Every ` ```nanyx ` block in the [language tour](../tour/variables), the [Getting started](../getting-started/basics)
pages, the [Standard Library](../stdlib/standard-library) pages, the [Custom patterns](../advanced/custom-patterns)
page and the [JSON cookbook](../cookbook/json-parsing) page is a test case: `RunTourExampleTests.fs` in
`Nanyx.Compiler.Wasm.Tests` reads their pages, and compiles and runs each block in the test process, as `nanyx run`
would run it as a script. A standard-library page's generated reference is left out (see
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

# Goals

- Every example in the language tour compiles and runs, and a test keeps it that way.
- A gap between the docs and the compiler is either fixed or filed, with the phase label that says where it is.
- The compiler stays fast enough to run on every keystroke in the editor.

# Plan

1. **Docs coverage beyond the tour.** The examples of the other sections (`advanced` but for Custom patterns,
   `cookbook` but for JSON parsing, and `coming-from-other-languages`) are not compiled, and can follow one page at a
   time. The blocks marked `skip=` wait on the issues they name.
2. **Parser error recovery.** Keep parsing after a syntax error so later errors (and later analysis) are not
   lost. Shared with the language server; it is stage 5 of [The parser](./parser#stage-5-error-recovery).
3. **Backend coverage.** Close the remaining value types and constructs the type checker accepts, such as unboxed
   floats ([Memory management](./memory-management#still-to-do)).
4. **Standard library.** Implement the documented modules, writing intrinsics only where Nanyx cannot
   ([Standard library implementation](./standard-library-implementation)).
