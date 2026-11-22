---
title: "Deferred errors"
description: "Deferring more kinds of error, and the questions deferral leaves open"
order: 22
---

What deferred errors do, which errors are deferred, and debug and release builds are described in
[Deferred errors](../tour/error-handling#deferred-errors) in the tour, and how the compiler defers them in
[Deferred errors](../deep-dives/compilation#deferred-errors) in the Deep dives section. This page keeps the
[wider deferral](#plan) and [questions](#open-questions) still open. It is part of the [compilation](./compilation)
workstream, and its issues carry the `compilation` label with `type checker` or `wasm backend` as they apply.

# Plan

Wider deferral, if it is wanted:

- **Type definitions.** An error in one, such as a field of an unknown type, is not deferred. A later version could
  make the type the error type.
- **Imports.** An import that is not found, an import cycle or an error inside an imported module is not deferred. A
  later version could defer the names the import brings in.
- **Syntax errors**, with parser recovery ([compilation](./compilation#plan)): a top-level definition that does not
  parse. The [principles](../meta/principles) say syntax errors stop a build.

# Tests

When there is a test runner, it should run every test in a debug build: a test that reaches a deferred error fails
with that error's message, and the others pass or fail on their own merits. This is what makes the feature useful
for red-green iteration. The proposed runner, `nanyx test`, does this; see
[Testing](./testing#deferred-errors).

# Open questions

- **Blame.** With constraints solved together, the error is reported at whichever constraint fails, which is not
  always the expression the programmer got wrong. The trap is at the same place as the diagnostic, so it is
  consistent, but it may trap later (or earlier) than the real mistake. Is that acceptable, or does deferral want
  blame that follows annotations, as some bidirectional checkers do?
- **Generic definitions.** A generic definition with an error in its body is instantiated once per use by the
  backend. Its traps are the same in each instance; confirm that monomorphisation does not need the definition's
  type to be free of the error type (`Unifier.errorType`) anywhere else.
