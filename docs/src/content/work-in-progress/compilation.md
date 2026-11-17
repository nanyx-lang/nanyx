---
title: "Compilation"
description: "The parser, type checker, backend, runtime and CLI: where they are and what comes next"
order: 21
---

This page frames the compilation workstream: what it covers, where the compiler stands and what comes next. Work is
tracked under the `compilation` label. How the compiler works today, what the backend compiles, the binary format and
how the docs' examples are tested, is in [Compilation](../compiler-internals/compilation) in the Compiler internals
section.

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
| Standard library | `stdlib/nanyx/`, and the intrinsics it needs; checked once at build time by `Nanyx.Compiler.CheckedLibrary` ([Standard library implementation](../compiler-internals/standard-library-implementation)) |

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
  whole module and solves it again as it goes (`Solving.unifyDetailed`). A list is solved on top of the last one
  solved, or of the checkpoint the type checker made once it generalised the def before (`Solving.checkpoint`), when
  that can't change the result. Defs that share a variable (mutual recursion, a use before the def is checked) and a
  module with a type error still solve afresh, newest first.

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
   ([Standard library implementation](../compiler-internals/standard-library-implementation)).

# Open questions

- **The standard library's reference pages** ([Its pages on the docs
  site](../compiler-internals/standard-library-implementation#its-pages-on-the-docs-site)):
  - A named parameter is written in a signature in the order of its name, not the order it is declared in, as hover
    writes it: `string.substring: (string, length: int, start: int)`.
  - `proc`'s signature is a long record of its keyword members, which says little to a reader.
