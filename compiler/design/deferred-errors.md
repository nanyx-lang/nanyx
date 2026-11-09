# Deferred errors

> **Status: in progress.** This page decides how a debug build keeps running in the face of name and type errors, as
> the [principles](../../docs/src/content/foundations/principles.md#gradual-feedback-not-blocked-flow) promise.
> Built: the type checker recovers from errors, they are compiled to traps, and `nanyx run`, `build` and `wasm`
> are debug builds with `--release` to refuse errors (stages 1 to 3 of the [plan](#plan)). Wider deferral is
> left for when it is wanted. It is part of the [compilation](compilation.md) workstream, and
> its issues carry the `compilation` label with `type checker` or `wasm backend` as they apply.

## Summary

A name or type error does not stop a debug build. The compiler reports it as usual, and compiles the expression
where it was found to a trap that reports the same diagnostic if it is ever reached. Everything around the error
compiles and runs normally, so the parts of a program that do type-check can be run and tested while the
parts that don't are being fixed.

A release build still refuses to compile a program with any error.

The approach is the one GHC takes with `-fdefer-type-errors`, and a pragmatic cousin of Hazel's marked holes: the
error becomes a node in the typed program, with a type that fits anywhere, and evaluating it fails.

## Where we are today

Any error stops the build, at three points:

- **Inference stops at an error.** `Infer.inferModuleWithEnv` returns `Ok` with a typed module or `Error` with
  diagnostics, never both. Inside it, an item or expression with an error is not checked further (the many
  `if errors.IsEmpty then` in `Infer.fs`).
- **Unification stops at the first failure.** `Unifier.unify` solves the module's constraints together and returns
  one `Error (message, span)` ([Library.fs](../Nanyx.Compiler/Library.fs), in `typecheckModule`). The editor sees
  one type error per module, and nothing typed after it.
- **The CLI stops on errors.** `nanyx run` and `nanyx build` print the diagnostics and exit with 1 if any is an
  error ([Program.cs](../Nanyx.Compiler.Cli/Program.cs)). There is no debug or release mode.

(This section is how things were when the design was written; the rest of the page says what changed.)

Non-exhaustive matches are a hard error today too, although the principles say a debug build should panic when
an unhandled case is reached. They fall under this design.

The backend's code is type-directed, which rules out the obvious shortcut: which instructions an operator becomes
depends on its operand types (`+` on strings calls the concatenation helper). Badly typed code cannot be lowered
"dynamically" without runtime type checks on every operation. That is a second backend to keep in step with the
first, for code that is about to be fixed; this design does not do it. (Every value is the same size, an `i32`,
which [makes traps easy](#errors-become-traps).)

## Design

### Every expression gets a type

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

- **The error type** is `Unifier.errorType`, a primitive no source can name (printed `?`), so the passes that
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
  unresolved. Conflicts do not yet bind anything to the error type: the expressions there are typed by their other
  constraints.
- **The result.** A module with type or name errors is typed all the same, with what did check:
  `CompileResult.Typed` holds it beside the errors, and `CompileResult.TypeChecked` says whether there were any. The
  analyzers only look at a module that type-checked. A module whose imports have errors is still not typed, and
  an import with errors fails its importer with them.
- **The editor** still reads the last text that type-checked for hover, completion and type hints while the current
  one does not (`OpenDocument.LastGood`). Reading the current text's partly typed module instead, with `?` where it
  failed, is a choice for the language server to make.

This stage changes nothing about what runs: the backend still refuses a program with errors. Its value is in the
editor, which sees every error in a module, and hover types and completions past the first one. It is also where
almost all of the work is.

### Errors become traps

A pass between the typed AST and code generation (or a step of the core IR, once
[memory management](memory.md) introduces one) rewrites each marked expression to `Panic(diagnostic)`. At run
time `Panic` writes the diagnostic to stderr, as the compiler printed it, and traps:

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

- **The values are uniform,** which makes the first rule above unnecessary. Every Nanyx value is an `i32` in the
  backend: an immediate or a pointer (a float is boxed), so a trap's `unreachable` stands in for any expression and
  the code after it still validates. Nothing is cut off after a trap.
- **The deferral pass** is `Compiler.deferErrors` in [Library.fs](../Nanyx.Compiler/Library.fs), run as the typed
  module's items are lowered for the backend. Each node in `FailedExprs` becomes `__panic("<message>")`. A conflict
  among the constraints is only known by where it was found, so it replaces the outermost expression at that span.
  A match in `UnhandledMatches` gets a last arm, `_ -> __panic(...)`, so it runs the cases it handles. The message
  is the diagnostic as `nanyx` prints it, with `(deferred from compile time)`.
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
  (`PackageBuild.ErrorsDeferred`) when each file's are: a file with an analyzer rule configured as an error, or an
  error in a directive, is not.

### Broken definitions

The unit of deferral above is an expression. A definition whose own type cannot be settled (its signature
mismatches its body, or its inferred type contains `TyError`) is deferred whole: its body is not compiled, and
every reference to it becomes a `Panic` with a diagnostic naming the definition and the original error. A
definition whose signature is sound (written down, or inferred without error) compiles normally, with traps only
where its body has errors. This keeps a broken helper from poisoning the types of the functions that call it, and
a call to it fails at the call, with a message that says why.

### What is deferred

| Error | Deferred? |
|-------|-----------|
| Unknown name, type mismatch, wrong number of arguments, missing field | Yes, as an expression |
| Unsatisfied context | Yes, as an expression (the call that needs the context traps) |
| Non-exhaustive match | Yes: the match traps on a case no arm handles |
| An error in a definition's type | Yes, as a definition |
| Syntax error | No, as the principles say; see [parser error recovery](compilation.md#plan) |
| Error in a type definition, such as a field of an unknown type | No, at first. A later version could make the type `TyError` |
| Import not found, import cycle, error inside an imported module | No, at first. A later version could defer the names the import brings in |

### Debug and release

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

When there is a test runner, it should run every test in a debug build: a test that reaches a deferred error fails
with that error's message, and the others pass or fail on their own merits. This is what makes the feature useful
for red-green iteration.

## Plan

1. **Type checker recovery.** Add `TyError`; make unknown names and failed constraints recorded diagnostics rather
   than early returns; let the unifier carry on past a failure; return a typed module with its diagnostics. Tests:
   a module with several independent errors reports all of them, and one mistake reports one error, not a cascade.
   The backend still refuses programs with errors.
   - [x] Carry on past a def that fails to check, and past a conflict among the constraints.
   - [x] Recover within an expression: the error type, and `FailedExprs` for the nodes that failed.
   - [x] Return the typed module alongside its diagnostics.
2. **Deferral pass and `Panic`.** Mark erroneous expressions and definitions in the typed AST, rewrite them to
   `Panic`, add the runtime helper (write to stderr, then `unreachable`), and cut sequences off after a panic.
   Make non-exhaustive matches deferred too.
   - [x] Done, with no cutting off needed (see [Errors become traps](#errors-become-traps)); `nanyx run` on a file
     runs a program whose errors are all deferred.
3. **CLI modes.** Make `run` and `build` debug, add `--release`, and print the deferred-error count.
   - [x] `run` on a file, with the count.
   - [x] `run` on a package, `build` and `wasm`, each with `--release` where it writes a program.
   - [x] Report reaching a deferred error without Wasmtime's trap report after it.
4. **Wider deferral,** if it is wanted: type definitions and imports, then (with parser recovery) a top-level
   definition that does not parse.

## Open questions

- **Exit status.** A debug run that never reaches a deferred error exits with the program's own status, for now, and
  a debug build that writes a program exits with 0. That is honest about the run, but makes it easy for a script or
  CI job to pass on a program that does not type-check: CI should use `--release`, or `nanyx check`. Should a debug
  build or run with errors exit non-zero instead?
- **Blame.** With constraints solved together, the error is reported at whichever constraint fails, which is not
  always the expression the programmer got wrong. The trap is at the same place as the diagnostic, so it is
  consistent, but it may trap later (or earlier) than the real mistake. Is that acceptable, or does deferral want
  blame that follows annotations, as some bidirectional checkers do?
- **Generic definitions.** A generic definition with an error in its body is instantiated once per use by the
  backend. Its traps are the same in each instance; confirm that monomorphisation does not need the definition's
  type to be free of `TyError` anywhere else.
