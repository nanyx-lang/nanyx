---
title: "Deferred errors"
description: "How a debug build keeps running in the face of name and type errors"
order: 22
---

Deferred errors are built: the type checker recovers from errors, they are compiled to traps, and `nanyx run`,
`build` and `wasm` are debug builds, with `--release` to refuse errors, as
[Building and running a package](../tour/packages#building-and-running-a-package) says. This page keeps how it is
built, and the [wider deferral](#plan) and [questions](#open-questions) still open. It is part of the [compilation](./compilation) workstream, and its issues
carry the `compilation` label with `type checker` or `wasm backend` as they apply.

# Summary

A name or type error does not stop a debug build. The compiler reports it as usual, and compiles the expression
where it was found to a trap that reports the same diagnostic if it is ever reached. Everything around the error
compiles and runs normally, so the parts of a program that do type-check can be run and tested while the
parts that don't are being fixed.

A release build still refuses to compile a program with any error.

The approach is the one GHC takes with `-fdefer-type-errors`, and a pragmatic cousin of Hazel's marked holes: the
error becomes a node in the typed program, with a type that fits anywhere, and evaluating it fails.

# Design

## Every expression gets a type

The type checker recovers from errors instead of stopping:

- **An error type.** `TyError` unifies with any type without failing and without binding anything, so one mistake
  does not cascade into errors about everything that uses the result.
- **Unknown names.** A name that does not resolve is reported, and becomes an error node of type `TyError`.
- **Failed constraints.** When unification fails, the unifier records the diagnostic against the constraint's
  span, binds the type variables involved to `TyError`, and carries on with the remaining constraints. Which of two
  conflicting constraints is blamed depends on the order they are solved, as it does today.
- **The result.** Checking a module always produces a typed module, alongside its diagnostics. The expressions an
  error was reported at are marked in the typed AST, with the diagnostic.

How it is built:

- **The error type** is `Unifier.errorType`, a primitive no source can name (printed `???`, as the [placeholder for unwritten code](../getting-started/basics#unimplemented-code) is written), so the passes that
  handle types need no case of their own for it. Unifying it with a type variable binds the variable to it, as with
  any type; unifying it with anything else succeeds without binding anything. A variable bound to it therefore
  agrees with everything from then on, which is what stops one error cascading through the uses of a name.
- **An expression that fails to check** is recovered where it failed: `inferExpr` records its diagnostics in the
  state's `FailedExprs`, by its node, gives it the error type, and carries on. What inferring it did before it
  failed is dropped. The smallest failing expression is the one recorded: a call whose arguments check but whose
  callee is unknown is recorded whole, while an unknown name among its arguments is recorded alone. `FailedExprs`
  is also the list of nodes the deferral pass will rewrite.
- **What is done with an error-typed value** is not another error: reading a member of it, or piping it to a name
  attached to no type, gives the error type again (the pipe's arguments are still checked).
- **A def that fails to check** before its body is inferred (its annotation names an unknown type, say) is
  reported, and bound at the error type from then on, annotated or not, since its annotation may be what failed.
  It stays in the module, with its body (or, for a value, the whole def) recorded in `FailedExprs`.
- **A match that does not handle every case** is reported and typed as if it did, and recorded in
  `UnhandledMatches`.
- **A conflict among the constraints** is reported, and the constraints from where it was found are set aside
  before the rest are solved again (`Unifier.solve`). The constraints solved part-way through inference, to read a
  record's fields say, are solved the same way, so a conflict in one def does not leave the types in the next
  unresolved, and a later def's error does not hide it. A union that tries each of its members against the
  constraints after it reports a failure from further on (`unionFailure`) rather than blaming itself, so the
  conflict is reported in the def that made it, wherever the def is in the file
  ([#170](https://github.com/nanyx-lang/nanyx/issues/170)). Conflicts do not yet bind anything to the error type: the expressions there are typed by their other
  constraints.
- **The result.** A module with type or name errors is typed all the same, with what did check:
  `CompileResult.Typed` holds it beside the errors, and `CompileResult.TypeChecked` says whether there were any. The
  linters only look at a module that type-checked. A module whose imports have errors is still not typed, and
  an import with errors fails its importer with them.
- **The editor** reads the current text's partly typed module for hover, completion and type hints, with `???` where
  an error left a type unknown; only while the text does not parse does it read the last text that was typed
  (`OpenDocument.LastGood`). See [the language server](./language-server).

Recovery is also what the editor reads: it sees every error in a module, and hover types and completions past the
first one.

## Errors become traps

A pass between the typed AST and the core IR ([memory management](./memory-management)) rewrites each marked
expression to `Panic(diagnostic)`. At run time `Panic` writes the diagnostic to stderr, as the compiler printed it, and traps:

```
error E001: Type mismatch at src/main.nyx:10:5 (deferred from compile time)
  Expected type `int` but found type `string` in the expression `x + 1`
```

WebAssembly's `unreachable` fits wherever an instruction of any type is expected, so a trap can stand in for an
expression of any type. Two rules keep the rest of the function valid:

- **A panic does not return.** WebAssembly still validates the code after `unreachable`, so the code that would
  use the broken value cannot simply follow it: in `let x = <error>; x + 1`, `x` has no representation to load
  into `i64.add`. The pass treats `Panic` as diverging and cuts off the rest of its enclosing sequence, as it would
  after any expression of type `never`.
- **Effects to its left still happen.** Evaluation order is kept up to the error: `println("a"); f(bad)` prints
  `a` and then traps, and in `f(a(), bad, c())` the call `a()` runs and `c()` does not.

How it is built:

- **A trap stands for any value,** which makes the first rule above unnecessary. A trap's `unreachable` stands in for
  an expression of any type, an `i64` int as well as an `i32` (an immediate or a pointer; a float is boxed), and the
  code after it still validates. Nothing is cut off after a trap.
- **The deferral pass** is `DeferredErrors.deferErrors` in [DeferredErrors.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/DeferredErrors.fs), run as the typed
  module's items are lowered for the backend, from where inference found the errors (`DeferredErrors.Sites`). Each
  node in `FailedExprs` becomes `__panic("<message>")`. A conflict among the constraints is only known by where it
  was found, so the conflicts found at a span replace the outermost expression there. A match in `UnhandledMatches`
  gets a last arm, `_ -> __panic(...)`, so it runs the cases it handles. The message is the diagnostic as `nanyx`
  prints it, with `(deferred from compile time)`. The pass returns the diagnostics it placed, and an error counts as
  deferred only if it is one of them, its place included, not another with the same text.
- **`__panic`** lowers to a write of its message to stderr, then WASI's `proc_exit(1)`, then `unreachable` (which
  `proc_exit`, never returning, never reaches). The runner reports an exit as `RunError.Exited`, and the CLI exits
  with its code and nothing more: the error has already said why the program stopped. A trap is still a runtime
  error, with Wasmtime's report.
- **What the backend cannot lower because of an error.** The backend still looks at types in places: an
  interpolated value of the error type has no known way to print. A function body or top-level expression that
  holds a deferred error and that the backend cannot lower traps with the errors in it, as soon as it runs.
- **What cannot be deferred.** An error the pass places nowhere (one in a type definition, or a conflict found at a
  pattern) leaves `CompileResult.ErrorsDeferred` false, and the program is not run.
- **The CLI** is described in [Debug and release](#debug-and-release). A package's errors are deferred
  (`PackageBuild.ErrorsDeferred`) when each file's are: a file with a linter rule configured as an error, or an
  error in a directive, is not.

## Broken definitions

The unit of deferral above is an expression. A definition whose own type cannot be settled (its signature
mismatches its body, or its inferred type contains `TyError`) is deferred whole: its body is not compiled, and
every reference to it becomes a `Panic` with a diagnostic naming the definition and the original error. A
definition whose signature is sound (written down, or inferred without error) compiles normally, with traps only
where its body has errors. This keeps a broken helper from poisoning the types of the functions that call it, and
a call to it fails at the call, with a message that says why.

## What is deferred

| Error | Deferred? |
|-------|-----------|
| Unknown name, type mismatch, wrong number of arguments, missing field | Yes, as an expression |
| Unsatisfied context | Yes, as an expression (the call that needs the context traps) |
| Non-exhaustive match | Yes: the match traps on a case no arm handles |
| An error in a definition's type | Yes, as a definition |
| Syntax error | No, as the principles say; see [parser error recovery](./compilation#plan) |
| Error in a type definition, such as a field of an unknown type | No, at first. A later version could make the type `TyError` |
| Import not found, import cycle, error inside an imported module | No, at first. A later version could defer the names the import brings in |

## Debug and release

| Command | Errors |
|---------|--------|
| `nanyx run` | Debug: diagnostics go to stderr, then the program runs, with deferred errors as traps |
| `nanyx build`, `nanyx wasm` | Debug by default, like `run`, writing a program that traps where the errors were |
| `nanyx build --release`, `nanyx wasm --release` | Refuses any error, and writes nothing |
| `nanyx check` | Unchanged: reports diagnostics and runs nothing |

Before running a program with deferred errors, `run` says so in one line after the diagnostics (for example
"Running with 2 deferred errors"), so a run that happens to avoid them is not mistaken for a clean one. `build` and
`wasm` say so in the line that names what they wrote ("Built app (2 files) with 2 deferred errors: ..."). A debug
`build` of a package with no `@entry` writes nothing, so it is only a check, and errors fail it.

Every one of these still refuses an error that is not deferred.

**Exit status.** Deferred errors do not change it. A debug run exits with the program's own status: 0 if it never
reaches a deferred error and ends normally, 1 if it reaches one. A debug `build` or `wasm` that writes a program
exits with 0. The exit status says how the run or the build went, and the diagnostics say whether the program
type-checks. A script or CI job that must not pass on a program with errors uses `--release`, or `nanyx check`,
which exits with 1 on any error.

When there is a test runner, it should run every test in a debug build: a test that reaches a deferred error fails
with that error's message, and the others pass or fail on their own merits. This is what makes the feature useful
for red-green iteration. The proposed runner, `nanyx test`, does this; see
[Testing](./testing#deferred-errors).

# Plan

Wider deferral, if it is wanted: type definitions and imports, then (with parser recovery) a top-level definition
that does not parse.

# Open questions

- **Blame.** With constraints solved together, the error is reported at whichever constraint fails, which is not
  always the expression the programmer got wrong. The trap is at the same place as the diagnostic, so it is
  consistent, but it may trap later (or earlier) than the real mistake. Is that acceptable, or does deferral want
  blame that follows annotations, as some bidirectional checkers do?
- **Generic definitions.** A generic definition with an error in its body is instantiated once per use by the
  backend. Its traps are the same in each instance; confirm that monomorphisation does not need the definition's
  type to be free of `TyError` anywhere else.
