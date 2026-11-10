---
title: "Error messages"
description: "What diagnostics say and how they are shown"
order: 28
---

> **Status: outline.** This page frames the error messages workstream: what it covers, where the compiler is today
> and what comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked
> under the `error messages` label.

# Summary

[Principles](../foundations/principles#human-readable-errors) promises errors with a code,
a short title, a detailed message using the program's own names, suggestions, and fixes an editor can apply,
written in a matter-of-fact passive voice. This workstream is about meeting that promise: what each diagnostic
says, where it points, and how the CLI and the editor show it.

# Scope

- The catalogue of compiler errors: their codes, titles and wording.
- Positions: every error points at the code it is about, in the right file.
- Suggestions and fixes for the common errors ("did you mean", missing context, missing match case).
- Rendering in the CLI (`nanyx check`, `nanyx run`) and in the language server.
- A reference page in the docs for each error code.

The `Diagnostic` type itself (code, span, related locations, fixes) is designed in
[Linters: structured diagnostics](./linters#2-structured-diagnostics) and tracked there, because linters
need it first. This workstream fills it with content.

# Where the compiler is today

- **Diagnostics are structured** ([#97](https://github.com/nanyx-lang/nanyx/issues/97)): every compiler error has
  a code and title from the table in `Diagnostics.fs` (`DiagnosticCodes`), and the span, in its own file, of the
  node it is about. Few use related locations yet, and none has suggestions or fixes.
- **Some spans are approximate.** Field names, type annotations and attributes have no spans yet
  ([#96](https://github.com/nanyx-lang/nanyx/issues/96)), so errors about them are reported at the nearest node
  that has one. A type mismatch is reported where the constraint that failed was made, which is not always where
  the conflicting types came from.
- **Wording is inconsistent.** Messages range from `Unknown identifier 'x'` to `dbg expects a single argument`
  and `` `with` updates fields by name``; quoting, capitalisation and voice vary.
- **The CLI prints one line per diagnostic**, `file:line:col: error NYX0201: message`, with no source excerpt.
- **There are no suggestions or fixes.**

# Goals

- Every compiler error has a stable code and a title, and follows the voice in Principles.
- Every error carries a span and, where it helps, related locations ("expected because of this annotation").
- The CLI shows the file, the line of source and a marker under the span.
- The most common errors (unknown name, type mismatch, missing context, non-exhaustive match) offer a
  suggestion, and a fix where the edit is unambiguous.

# Plan

_To do._ Candidate items, not yet filed:

1. **Inventory.** List every error the parser and type checker can produce, with an example program for each.
2. **Codes and wording.** Allocate codes and rewrite messages to the house style, as each error moves to the
   structured `Diagnostic` (after [#97](https://github.com/nanyx-lang/nanyx/issues/97)).
3. **CLI rendering.** File, source excerpt and underline; colour when the output is a terminal.
4. **Suggestions.** "Did you mean" for unknown names; missing context and missing match cases, with fixes.
5. **Error reference.** A docs page per code, generated or checked against the compiler's table.

# Open questions

- One flat `NYX0001` sequence, or ranges per phase? (Also open in [Linters](./linters#open-questions).)
- Should the error reference pages live in the docs site and be linked from the LSP `codeDescription`?
- Are error message snapshots part of the compiler tests, so wording changes are reviewed deliberately?
