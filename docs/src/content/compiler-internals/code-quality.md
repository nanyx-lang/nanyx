---
title: "Code quality"
description: "How the compiler's code is structured, and why the contributor rules exist"
order: 15
---

The code quality workstream is about how the compiler and its tools are structured: file and function size, duplicated
code, dead code, and how the tests are laid out. It does not cover what the compiler does. The rules for
day-to-day changes are in [AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md); this document explains why they exist and records
where the code stands. Issues carry the
[`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) label and are on the
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1), ordered by its Stage field.

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

The first round of splits, from 2026-11-09 to 2026-11-16, brought `CodeGen.fs`, `Infer.fs`, the parser's `Program.fs`
(now `Expressions.fs` and the files around it), `Library.fs` and `CoreIrLower.fs` under the limit, broke their longest
functions and match arms into named ones, and moved the large test files into `Features/<Area>/` files. The issues
that did it are closed under the `code quality` label.

`Infer.fs` is mostly one recursive chain, from `inferExpr` to `inferBlock`, whose long arms are `and` functions of
their own (`inferOperator`, `inferPipe`, `inferMatch`, `inferCall`); `inferMatch` and `inferCall` are each about 300
lines. The `Compiler` workflow lists the files over the limit on every run, and the
[`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) label lists the open problems.

# The daily review

A scheduled review runs once a day. It does five things:

1. Measures file sizes on `main`.
2. Reads the last day's commits to the compiler sources.
3. Files new problems as `code quality` issues, after checking each one in the code.
4. Adds each new issue to the Nanyx workstreams project.
5. Proposes a work session for each of up to three open issues that nobody has claimed, choosing issues
   that touch different files.

It looks for the problems the rules in AGENTS.md are meant to prevent:

- compiler source files growing past 1,500 lines (the standard library's `.nyx` files are exempt)
- new copies of existing helpers
- new string matches on desugared names
- new `failwith` calls in the backend
- tests added outside the `Features/` layout

The `Compiler` workflow also lists the compiler's F# and C# files over 1,500 lines on every run: each is a warning on
the run, and in a table in the run's summary. They don't fail the run.
