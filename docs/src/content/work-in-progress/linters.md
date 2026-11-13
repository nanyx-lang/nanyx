---
title: "Linters: compiler design"
description: "What the compiler provides so that linters can be built on it"
order: 29
---

The substrate linters stand on is built: a span on every node, structured diagnostics, the semantic model, and the
internal linter host that runs the official linter. The user-facing model is described in
[Linters](../advanced/linters), and the official linter's rules in
[Official style linter](../standard-tools/official-linter). What is still open is the
[public contract](#5-the-public-contract-later) for linters written in Nanyx, linting [code with errors](#code-with-errors),
and the [open questions](#open-questions).

# Summary

Linters inspect a checked module and report diagnostics that are not compile errors: unused bindings,
unreachable branches, style rules. They are configured per project, and show up in the editor next to compiler
errors. The docs promise that they can be written in Nanyx.

# Goals

- Linter diagnostics are positioned exactly (start and end), in the right file, and can carry fixes.
- Every diagnostic, compiler or linter, has a stable code, so projects can configure, promote or suppress it.
- Linters see the program as the user wrote it, with types and resolved names.
- A linter can never break compilation: a crashing linter produces a diagnostic, not a failed build.
- The compiler itself emits only errors (see [No warnings](../foundations/principles)).

# Design

## 2. Structured diagnostics

> **Done** in [#97](https://github.com/nanyx-lang/nanyx/issues/97), except that few compiler errors offer
> suggestions or fixes yet: the first fix is the import an unknown name from the standard library needs
> ([#497](https://github.com/nanyx-lang/nanyx/issues/497)). The language server offers fixes as quick fixes
> ([#284](https://github.com/nanyx-lang/nanyx/issues/284)).

The diagnostic follows the error anatomy in [Principles](../foundations/principles#human-readable-errors):
a code, a short title, a detailed message, suggestions and fixes.

```fsharp
type Severity =
    | Critical   // compilation cannot proceed
    | Error
    | Warning
    | Info
    | Hint

type TextEdit = { Span: SourceSpan; NewText: string }

type Fix = { Title: string; Edits: TextEdit list }

type Diagnostic =
    { Code: string                              // "NYX0001", or "<linter>/<rule>" for linter diagnostics
      Severity: Severity
      Title: string                             // short and generic: "Type mismatch"
      Message: string                           // detailed, with the actual names
      Span: SourceSpan option                   // None only for whole-compilation problems
      Related: (SourceSpan * string) list       // "first defined here", "expected because of this annotation"
      Suggestions: string list
      Fixes: Fix list }
```

Rules:

- The compiler emits only `Critical` and `Error`. `Warning`, `Info` and `Hint` exist for linters.
- Compiler codes are `NYX` plus four digits, allocated in a single table in `Diagnostics.fs`
  (`DiagnosticCodes`) so they are stable and documentable. They are numbered by phase: `NYX00xx` for reading a
  module and its imports (syntax, imports, attributes), `NYX01xx` for resolving names, `NYX02xx` for types. Each
  entry gives the code its title and severity. Linter codes are `<linter>/<rule>` (`default/unusedBindings`),
  matching the manifest keys in the docs.
- `Critical` is for a compilation that has nothing to check (the file does not exist); every other compiler
  diagnostic is an `Error`.
- The language server maps `Span` to the LSP range, `Code` to `code`, `Related` to `relatedInformation` and
  `Fixes` to code actions.

## 3. The semantic model

Linters, hover and go-to-definition all ask the same questions: what is the type of this node, and what does
this name refer to. The semantic model answers them for one module:

```fsharp
type SymbolKind =
    | TopLevelValue | LocalValue | Parameter | PatternVariable
    | ImportedModule | TypeName | ContextMember

type Symbol =
    { Id: SymbolId
      Name: string
      Kind: SymbolKind
      Declaration: SourceSpan
      IsExported: bool }

type SemanticModel =
    { Syntax: Module                          // the module as written: before desugaring, attributes included
      Types: Map<NodeId, Ty>                  // resolved after unification
      Symbols: Map<SymbolId, Symbol>
      References: Map<NodeId, SymbolId>       // every identifier, call, pipe target, member access and qualified name
      Lowered: Map<NodeId, NodeId list> }     // surface node -> desugared nodes it produced
```

From this, "unused" is "a symbol with no references", and the language server's go-to-definition is
`References` followed by `Symbol.Declaration`.

How it is produced:

- **Name resolution is recorded, not just performed.** Inference already looks every name up; it records the
  `SymbolId` it found against the node. Imports become `ImportedModule` symbols, and a qualified access such as
  `m.sqrt` records a reference to both the import and the imported definition. Type definitions and contexts are
  `TypeDefinition` symbols (built as that, rather than `TypeName`, which is a case of `TypeExprKind`), and a type
  expression that names one records a reference by its own node. A context's members are `ContextMember` symbols,
  declared at their names in the context's record type (the parser records a record type's field names by its
  node), and carried with the context's `TypeDefInfo` so an imported context's members keep their module's symbols.
- **Desugaring keeps provenance** (done in [#99](https://github.com/nanyx-lang/nanyx/issues/99)). Every node
  `desugar` and `applyAttributes` synthesize gets the span of the syntax it replaced (`Expression.from`), and
  `Lowered` records which expressions were made for which node as written, in the order they were made
  (`WorkflowDesugaring.desugarWithDiagnostics`, and the call `applyAttributes` makes for `@entry`, by the attribute's
  node). One made for a made-up node counts as made for the node that one was. Linters walk `Syntax`; the type of a
  workflow bind is found through `Lowered`: its first expression is the continuation, which takes the bound value.
  `CompileResult.Lowered` has the same table. Desugaring an `except` (`Except.asMatch`) records nothing yet. This is
  much cheaper than teaching inference to check surface syntax directly, and it can be replaced by that later
  without changing what linters see.
- **Import inlining stays where it is.** `CheckedModule` already separates the module's own items from the ones
  inlined for the backend; linters read the semantic model of the module's own items, so inlining does not hide
  anything once references to imports are recorded.

Whether `TypedExpr` survives alongside the side tables, or is replaced by them, is an implementation choice for
the first issue that touches it. Side tables keyed by `NodeId` avoid keeping two trees in step.

## 4. The linter host (internal)

The host is `Linters.fs` ([#100](https://github.com/nanyx-lang/nanyx/issues/100)). The rules of the official
`default` linter are described in [Official style linter](../standard-tools/official-linter). What the host settled:

- Only the module being compiled is analyzed. An imported module is analyzed when it is compiled itself, so its
  diagnostics are not repeated in every importer (the language server would show them at the top of each one).
- A failing linter is reported under a rule every linter has, `<linter>/linterFailed`, a warning by
  default, so it can be configured or turned off like any rule. Throwing, or reporting a rule the linter does
  not declare, both count as failing, and the linter's other reports for the module are left out.
- The configuration is `LinterOptions` (the linters to run, and severities by code), passed to
  `Compiler.compileWith` and its file and source variants. A package's `.nyxp` sets it with an
  `[linters.<name>]` table per linter to run ([#279](https://github.com/nanyx-lang/nanyx/issues/279)), read by
  `nanyx build`, `nanyx run` and the language server; without the table, the linter does not run. A file
  outside any package turns linters on with `::linters` directives in the same way, and without them runs
  none. Linters are opt-in everywhere, including `Compiler.compile` and its variants.

Before any public contract, linters are F# values compiled into the compiler:

```fsharp
type Rule =
    { Id: string                    // "unusedBindings"
      Title: string
      DefaultSeverity: Severity
      Description: string }

type Report =
    { Rule: string
      Span: SourceSpan
      Message: string
      Related: (SourceSpan * string) list
      Fixes: Fix list }

type Linter =
    { Name: string                  // "default"
      Rules: Rule list
      Lint: SemanticModel -> Report list }
```

The host:

- runs after a module type-checks, once per user module (not standard-library modules);
- looks up each report's severity in the configuration (rule default, overridden per project, `none` drops it),
  and stamps the code `<linter>/<rule>`;
- catches a linter exception and turns it into one diagnostic naming the linter, so a broken linter never
  breaks the build;
- returns linter diagnostics in `CompileResult.Diagnostics` alongside compiler ones, so `nanyx check`,
  `nanyx run` and the language server need no special handling.

A rule promoted to `error` fails `nanyx run` and `build` like a compiler error does.

## 5. The public contract (later)

Once the internal linters have shaken out the semantic model, the public contract can be designed with
evidence rather than guesses. The direction so far:

- Linters are Nanyx packages, compiled to WebAssembly and run at compile time on the embedded Wasmtime engine
  the compiler already ships.
- They see the semantic model through query functions (type of a node, symbol of a reference, references to a
  symbol, children of a node) rather than a serialized copy of the whole tree. Queries keep the compiler's internal
  representation free to change, and avoid copying large trees across the WASM boundary.
- The contract is versioned from day one, and a linter declares the version it targets.

# Code with errors

Linters only look at a module that type-checked. Since [deferred errors](./deferred-errors), a module with type or
name errors is typed all the same, with what did check, so linters could run on it. The cost of not doing so is in
the editor: linter diagnostics disappear while the file has a type error, and reappear when it is fixed. A module
that does not parse also needs parser error recovery. Linters that run on such code must not assume every node has
a type: `Types` is a map, and a missing entry means "unknown".

# Plan

**Public linters.** Design the contract for Nanyx-authored linters
([#107](https://github.com/nanyx-lang/nanyx/issues/107)), based on what the internal linters taught us.

# Open questions

- **Suppression in source.** An attribute (`@allow(default/unusedBindings)`) on a definition, a comment form, or
  only the convention that a `_`-prefixed name is intentionally unused? The docs already use `_` patterns.
- **Linters across modules.** "Exported but never used anywhere in the project" needs every module's semantic
  model at once; per-module analysis is enough for the first rules.
- **Fix application.** Fixes are text edits; does the CLI get a `--fix`, or are fixes editor-only at first?
