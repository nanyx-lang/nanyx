# Compilation: workstream

> **Status: outline.** This page frames the compilation workstream: what it covers, where the compiler is today and
> what comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked
> under the `compilation` label.

## Summary

The compilation workstream is everything between a `.nyx` file and a running program: the parser, the type
checker, the WebAssembly backend, the runtime that hosts the output on Wasmtime, and the `nanyx` CLI. Its measure
of progress is simple: a program the documentation describes should parse, type-check and run.

## Scope

| Area | Project |
|------|---------|
| Parser | `Nanyx.Parser` |
| Type checker | `Nanyx.Compiler` (`Infer.fs`, `Unifier.fs`, `Library.fs`) |
| WebAssembly backend | `Nanyx.Compiler.Wasm` |
| Runtime | `Nanyx.Compiler.Runtime` |
| CLI | `Nanyx.Compiler.Cli` |
| Standard library | `stdlib/nanyx/`, and the intrinsics it needs |

Out of scope here, with their own workstreams:

- What diagnostics say and how they are shown: [Error messages](error-messages.md).
- Spans, symbols and the semantic model, when they are built for linters: [Linters](linters.md). The
  refactors land in this codebase, but the design and tracking live there.
- Editor features: [Language server](language-server.md).

## Where the compiler is today

- **Features reach the three phases unevenly.** A construct is often parsed before it type-checks, and
  type-checks before the backend can generate code for it. Gaps found so far were filed per phase under the
  `parser`, `type checker` and `wasm backend` labels; most of those are closed.
- **The backend is an MVP.** It covers integers, strings, tags, lists, records, closures and pattern matching
  (see the WebAssembly section of the [compiler README](../README.md)). Floats and other value types are partial.
- **Type and name errors are deferred.** The type checker reports every error in a module, and a debug build
  (`nanyx run`, `build`, `wasm`) runs in spite of them, reporting each if the program reaches it; `--release`
  refuses them. See [deferred-errors.md](deferred-errors.md).
- **A parse error stops everything.** There is no error recovery, so one syntax error hides every problem after
  it. The language server needs recovery too.
- **Most of the standard library is designed but not written.** The docs describe it; `stdlib/nanyx/` holds a
  small part of it.

## Goals

- Every example in the language tour compiles and runs, and a test keeps it that way.
- A gap between the docs and the compiler is either fixed or filed, with the phase label that says where it is.
- The compiler stays fast enough to run on every keystroke in the editor.

## Plan

_To do._ Candidate items, not yet filed:

1. **Docs coverage.** Run every tour example through `nanyx run` in a test, and file what fails.
2. **Parser error recovery.** Keep parsing after a syntax error so later errors (and later analysis) are not
   lost. Shared with the language server.
3. **Backend coverage.** Close the remaining value types and constructs the type checker accepts.
4. **Standard library.** Implement the documented modules, writing intrinsics only where Nanyx cannot.
5. **Memory management.** Free memory with Perceus reference counting, which brings a core IR between the typed
   AST and WebAssembly. Designed in [memory.md](memory.md).
6. **Tail calls.** Keep the language's promise that a tail call uses no stack, with `return_call`. Designed in
   [tail-calls.md](tail-calls.md).
7. **Deferred errors.** Keep a debug build running in the face of name and type errors, by compiling each error
   to a trap where it was found. Designed in [deferred-errors.md](deferred-errors.md).

## Open questions

- Should the tour examples become compiler fixtures directly, or be extracted from the docs at test time?
- How far should parser recovery go before the AST needs explicit error nodes?
