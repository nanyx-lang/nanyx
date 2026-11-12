---
title: "Code quality"
description: "How the compiler's code is structured, and why the contributor rules exist"
order: 33
---

This workstream is about how the compiler and its tools are structured: file and function size, duplicated
code, dead code, and how the tests are laid out. It does not cover what the compiler does. The rules for
day-to-day changes are in [AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md); this document explains why they exist and records
where the code stands. Issues carry the
[`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) label and are on the
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1), ordered by its Priority field.

# Why

Feature work lands dozens of commits a day, and most of them touch the same few files. When a file runs to
thousands of lines, several things go wrong:

- Two features that don't interact still conflict in it.
- The same helper gets written again because the first copy is hard to find, and the copies drift apart.
- Nobody notices dead code.

The first review, on 2026-11-09, found evidence of all three:

- **Copies that drifted.** The backend had more than thirteen hand-written walks over the syntax tree, and
  they no longer agreed with each other.
- **One table written four times.** Operator precedence was written out twice in the parser and twice more
  in the formatter.
- **Duplicated tests.** Eight parser tests existed twice.
- **Real bugs.** Two backend memory bugs were hiding in the size of the file:
  [#375](https://github.com/nanyx-lang/nanyx/issues/375) and
  [#376](https://github.com/nanyx-lang/nanyx/issues/376).

# How refactoring lands

These files change too fast for a long-lived refactor branch to ever merge, so every refactor follows the
same rules:

- **One move per PR.** Each PR moves one coherent group of functions into its own file, by cut and paste.
- **Branched from `main`, merged quickly.** Each PR merges within hours, and PRs are never stacked.
- **Nothing observable changes.** Behaviour, generated WAT, formatter output and test counts stay the
  same. A bug that a move turns up is fixed in its own PR.

**The limit on splitting F#.** A mutually recursive `let rec … and` chain cannot be split across files. So
a split moves everything around the chain into earlier or later files, and leaves the chain itself where
it is. The chain can then be shortened by turning its long match arms into separate `and` functions.

# Where it stands

**2026-11-09, at a19d2de.** The first review measured these files:

| File | Lines |
|---|---|
| `CodeGen.fs` | 4,621 |
| `Tests.fs` | ~3,300 |
| `Infer.fs` | 3,054 |
| `Program.fs` (parser) | 2,593 |
| `RunTests.fs` | ~1,775 |
| `Library.fs` | 1,435 |

**2026-11-10, at 864ed61.** After the first round of splits:

| File | Lines | What changed |
|---|---|---|
| `CodeGen.fs` | 1,331 | Lambda lifting, runtime helpers, interpolation and the core IR now have their own files |
| `Infer.fs` | 2,597 | `InferState.fs` and `Visibility.fs` moved out |
| `Program.fs` (parser) | 1,481 | `Ast.fs`, `ParseState.fs`, `Lexical.fs`, `Refs.fs`, `Patterns.fs` and `Types.fs` moved out |
| `Library.fs` | 1,557 | Grew. Its split is [#365](https://github.com/nanyx-lang/nanyx/issues/365) |

The large test files are now split into `Features/<Area>/` files.

**2026-11-12.** Splitting `Infer.fs` around its recursive chain
([#364](https://github.com/nanyx-lang/nanyx/issues/364)), which had grown back to 3,269 lines:

| File | Lines | What changed |
|---|---|---|
| `Infer.fs` | 1,462 | `TypeResolution.fs`, `Contexts.fs` and `ContextInstances.fs` moved out before the chain, `InferModule.fs` after it |

What is left in `Infer.fs` is a few small helpers and the recursive chain, from `inferExpr` to `inferBlock`. Most of
the chain is `inferExprKind`, a single match of about 1,000 lines; its long arms can become `and` functions of their
own.

The [`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) label lists the open problems.

# The daily review

A scheduled review runs once a day. It does five things:

1. Measures file sizes on `main`.
2. Reads the last day's commits to the compiler sources.
3. Files new problems as `code quality` issues, after checking each one in the code.
4. Adds each new issue to the Nanyx workstreams project.
5. Proposes a work session for each of up to three open issues that nobody has claimed, choosing issues
   that touch different files.

It looks for the problems the rules in AGENTS.md are meant to prevent:

- files growing past 1,500 lines
- new copies of existing helpers
- new string matches on desugared names
- new `failwith` calls in the backend
- tests added outside the `Features/` layout
