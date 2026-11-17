---
title: "Deferred errors"
description: "Deferring more kinds of error, and the questions deferral leaves open"
order: 22
---

What deferred errors do, which errors are deferred, and debug and release builds are described in
[Deferred errors](../tour/error-handling#deferred-errors) in the tour. This page keeps the
[wider deferral](#plan) and [questions](#open-questions) still open. It is part of the [compilation](./compilation)
workstream, and its issues carry the `compilation` label with `type checker` or `wasm backend` as they apply.

The approach is the one GHC takes with `-fdefer-type-errors`, and a pragmatic cousin of Hazel's marked holes: the
error becomes a node in the typed program, with a type that fits anywhere (`???`), and evaluating it fails. The
deferral pass is `DeferredErrors.deferErrors` in `DeferredErrors.fs`.

A failed call keeps its callee's declared result. A call that fails for a reason outside its callee's type (a missing
context, a callee a module block doesn't export, an argument passed by position for a named parameter) is recorded as
failed in `inferCall` and checked on with the callee's type, and one given an argument of the wrong type or the wrong
number of them is checked against the callee's type as any call is (#1947). Only what a failed argument would have
decided is `???`: `list.map(xs, 5)` is a `list(???)`. A call whose callee is unknown, ambiguous or a context instance is
`???` whole.

# Plan

Wider deferral, if it is wanted:

- **Type definitions.** An error in one, such as a field of an unknown type, is not deferred. A later version could
  make the type the error type.
- **Imports.** An import that is not found, an import cycle or an error inside an imported module is not deferred. A
  later version could defer the names the import brings in.
- **Syntax errors**, with parser recovery ([compilation](./compilation#plan)): a top-level definition that does not
  parse. The [principles](../foundations/principles) say syntax errors stop a build.
- **Conflicts among constraints** bind to the error type only the variables of a failed constraint's expected side
  that nothing else fixes, solved after everything else once the module is checked (`Solving.solveLeavingUnknown`):
  a failed argument leaves unknown what it would have decided of the call's result. The expressions where a conflict
  was found are otherwise typed by their other constraints, and the deferral pass replaces the outermost expression at
  the conflict's span.

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
