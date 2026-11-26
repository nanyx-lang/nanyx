---
title: "Compilation"
description: "The parser, type checker, backend, runtime and CLI: where they are and what comes next"
order: 21
---

This page frames the compilation workstream: what it covers, where the compiler stands and what comes next. Work is
tracked under the `compilation` label. How the compiler works today, what the backend compiles, the binary format and
how the docs' examples are tested, is in [Compilation](../deep-dives/compilation) in the Deep dives
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
| Standard library | `stdlib/nanyx/`, and the intrinsics it needs; checked once at build time by `Nanyx.Compiler.CheckedLibrary` ([Standard library implementation](../deep-dives/standard-library-implementation)) |

Out of scope here, with their own workstreams:

- What diagnostics say and how they are shown: [Compiler errors](../standard-tools/errors), with the work tracked
  under the [`error messages`](https://github.com/nanyx-lang/nanyx/labels/error%20messages) label.
- Spans, symbols and the semantic model, when they are built for linters: [Linters](./linters). The
  refactors land in this codebase, but the design and tracking live there.
- Editor features: [Language server](../deep-dives/language-server), with the work tracked under the
  [`lsp`](https://github.com/nanyx-lang/nanyx/labels/lsp) label.

# Where the compiler stands

- **Features can reach the three phases unevenly.** A construct may parse before it type-checks, and type-check
  before the backend can generate code for it. Gaps are filed per phase under the `parser`, `type checker` and
  `wasm backend` labels.
- **A syntax error hides the rest of its item.** The parser reads on at the next top-level item, but not inside the
  item the mistake is in, so later problems in that item are not reported
  ([#2729](https://github.com/nanyx-lang/nanyx/issues/2729)).
- **Some defs are solved afresh.** A list of constraints is solved on top of the last one solved, or of a checkpoint,
  when that can't change the result ([Solving constraints](../deep-dives/compilation#solving-constraints)). Defs that
  share a variable (mutual recursion, a use before the def is checked) solve afresh, newest first.

# Goals

- Every example in the docs compiles and runs, and a test keeps it that way.
- A gap between the docs and the compiler is either fixed or filed, with the phase label that says where it is.
- The compiler stays fast enough to run on every keystroke in the editor.

# Plan

1. **The docs' skipped examples.** Every page's examples are compiled and run
   ([Docs examples](../deep-dives/compilation#docs-examples)); the blocks marked `skip=` wait on the issues they name.
2. **Parser error recovery inside an item.** The parser reads past a syntax error to the next top-level item, so
   later errors and later analysis are not lost ([The parser](../deep-dives/parser) in Deep dives).
   Recovering inside an item, at the next statement or arm, is
   [#2729](https://github.com/nanyx-lang/nanyx/issues/2729).
3. **Backend coverage.** Close the remaining value types and constructs the type checker accepts.
4. **Standard library.** Implement the documented modules, writing intrinsics only where Nanyx cannot
   ([Standard library implementation](../deep-dives/standard-library-implementation)).

# Open questions

- **The standard library's reference pages** ([Its pages on the docs
  site](../deep-dives/standard-library-implementation#its-pages-on-the-docs-site)):
  - A named parameter is written in a signature in the order of its name, not the order it is declared in, as hover
    writes it: `string.substring: (string, length: int, start: int)`.
  - `proc`'s signature is a long record of its keyword members, which says little to a reader.
