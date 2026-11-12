---
title: "Compilation"
description: "The parser, type checker, backend, runtime and CLI: where they are and what comes next"
order: 21
---

> **Status: outline.** This page frames the compilation workstream: what it covers, where the compiler is today and
> what comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked
> under the `compilation` label.

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
| Standard library | `stdlib/nanyx/`, and the intrinsics it needs |

Out of scope here, with their own workstreams:

- What diagnostics say and how they are shown: [Error messages](./error-messages).
- Spans, symbols and the semantic model, when they are built for linters: [Linters](./linters). The
  refactors land in this codebase, but the design and tracking live there.
- Editor features: [Language server](./language-server).

# Where the compiler is today

- **Features reach the three phases unevenly.** A construct is often parsed before it type-checks, and
  type-checks before the backend can generate code for it. Gaps found so far were filed per phase under the
  `parser`, `type checker` and `wasm backend` labels; most of those are closed.
- **The backend is an MVP.** It covers integers, strings, tags, lists, records, closures and pattern matching
  (see [What the backend compiles](#what-the-backend-compiles)). Floats and other value types are partial.
- **Type and name errors are deferred.** The type checker reports every error in a module, and a debug build
  (`nanyx run`, `build`, `wasm`) runs in spite of them, reporting each if the program reaches it; `--release`
  refuses them. See [Deferred errors](./deferred-errors).
- **A parse error stops everything.** There is no error recovery, so one syntax error hides every problem after
  it. The language server needs recovery too.
- **Most of the standard library is designed but not written.** The docs describe it; `stdlib/nanyx/` holds a
  small part of it; see [Standard library implementation](./standard-library-implementation).

## What the backend compiles

The parser, type checker, WebAssembly backend and runtime are libraries; the `nanyx` CLI is the only executable
(see [The `nanyx` command](./command-line)). The backend writes WebAssembly text from the core IR (see
[Memory management](./memory-management#core-ir)) and today covers:

- top-level `def` values exported as wasm functions; top-level expressions (and the `@entry` definition) run from a
  WebAssembly start function
- integer literals and integer binary operators (`+`, `-`, `*`, `/`, `%`, comparisons)
- bare tag literals (`#ok`, `#error`, etc.) lowered to deterministic, odd `i32` codes; a tag with a payload
  (`#some(x)`) is a pointer to a heap cell of (code, payload)
- function calls to known module defs, and closures
- expression blocks with local `def` assignments (`local.set`/`local.get`)
- strings are pointers to length-prefixed UTF-8 bytes; `++` and `==` on strings call emitted helpers
- list literals, `++` on lists, and list patterns (`[]`, `[x, ...rest]`, `[1, y]`) over heap-allocated cons cells
- every object on the heap has a reference count in its header, and the compiler inserts `dup` and `drop` (Perceus
  reference counting), so an object is freed once nothing refers to it, and one a `match` takes apart and holds
  the only reference to is rewritten in place into a new one of the same size (see
  [Memory management](./memory-management))
- `dbg(...)` of integers, floats, strings and bools (`true`/`false`), and `nanyx/console`, write to stdout through
  WASI `fd_write`; a value of a union of tags is printed as `"{value}"` would print it
- `dbg(#tag)` and `dbg(#tag(n))` of an int `n` call a host import (`env.dbg_tag_<tag>`,
  `env.dbg_tag_payload_<tag>`), which the runtime provides
- a function that requires a context (`[Console]`) takes its members as function-table slots; a
  `use` binding supplies them, otherwise a module-level function of the member's name does (that is how
  the runtime's `Console` reaches `@entry`); a function value whose type requires contexts, a lambda or a function
  passed as a value (`list.map(xs, println)`), is passed their members at each call, as they are where it is called
  (see [Context calls](./context-calls#functions-that-require-contexts-as-values))

A construct the backend cannot lower yet is reported as an `UnsupportedByBackend` error where it is written (see
[Memory management](./memory-management#core-ir)).

# Goals

- Every example in the language tour compiles and runs, and a test keeps it that way.
- A gap between the docs and the compiler is either fixed or filed, with the phase label that says where it is.
- The compiler stays fast enough to run on every keystroke in the editor.

# Plan

_To do._ Candidate items, not yet filed:

1. **Docs coverage.** Run every tour example through `nanyx run` in a test, and file what fails.
2. **Parser error recovery.** Keep parsing after a syntax error so later errors (and later analysis) are not
   lost. Shared with the language server.
3. **Backend coverage.** Close the remaining value types and constructs the type checker accepts.
4. **Standard library.** Implement the documented modules, writing intrinsics only where Nanyx cannot.
5. **Memory management.** Free memory with Perceus reference counting, which brings a core IR between the typed
   AST and WebAssembly. Designed in [Memory management](./memory-management). **Done**, with reuse in place.
6. **Tail calls.** Keep the language's promise that a tail call uses no stack, with `return_call`. Designed in
   [Tail calls](./tail-calls).
7. **Deferred errors.** Keep a debug build running in the face of name and type errors, by compiling each error
   to a trap where it was found. Designed in [Deferred errors](./deferred-errors).
8. **Closed and open records.** A record type has exactly the fields it names, unless it says `...` for more.
   Designed in [Closed and open records](./closed-and-open-records).

# Open questions

- Should the tour examples become compiler fixtures directly, or be extracted from the docs at test time?
- How far should parser recovery go before the AST needs explicit error nodes?
