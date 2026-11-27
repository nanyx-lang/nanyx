---
title: "Linters: compiler design"
description: "The public contract for linters written in Nanyx, and linting code with errors"
order: 29
---

How linters are configured and what they report is described in [Linters](../advanced/linters), and the official
linter's rules, `@allow` and what happens when the linter fails in
[Official style linter](../standard-tools/official-linter). The official linter is F#, compiled into the compiler and
run by its linter host (`Linters.fs`). What is still open is the [public contract](#the-public-contract) for linters
written in Nanyx, linting [code with errors](#code-with-errors), and the [open questions](#open-questions).

# What a linter sees

Linters, hover and go-to-definition all ask the same questions: what is the type of this node, and what does this
name refer to. The public contract is to expose the compiler's answers, which it keeps for one module in side tables
keyed by node: in `TypedModule`
(`TypedAst.fs`), the type of each node (`ExprTypes`), each symbol, with its kind and declaration (`Symbols`), and the
symbol each name refers to (`References`); and in the checked module (`CheckedModule.fs`), the module as written,
before desugaring, and the nodes desugaring made for each node as written (`Lowered`). Beside them are what each call requires: the callee's type at the call (`CalleeTypes`), what provides
each context to it (`ContextProviders`), the instances it finds (`ContextInstances`), and the contexts of the defs it
may call (`ContextTypes`, `ImportedContextTypes`). The official linter reads these directly.

# The public contract

Decided by the maintainer as the design ([#107](https://github.com/nanyx-lang/nanyx/issues/107)), with the details
(the query functions, the diagnostic and fix types, the version scheme, the time budget per keystroke) still to be
written here:

- Linters are Nanyx packages, compiled to WebAssembly and run at compile time on the embedded Wasmtime engine
  the compiler already ships. The official linter becomes one too ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343),
  [Shipped packages](../deep-dives/shipped-packages#why-the-official-ones-are-ordinary-packages)).
- They see what the compiler knows about a module through query functions (type of a node, symbol of a reference,
  references to a symbol, children of a node) rather than a serialized copy of the whole tree. Queries keep the
  compiler's internal representation free to change, and avoid copying large trees across the WebAssembly boundary.
- The contract is versioned from day one, and a linter declares the version it targets.
- A fix is a list of text edits, applied by the editor and by `nanyx check --fix`
  ([#3037](https://github.com/nanyx-lang/nanyx/issues/3037)).
- A linter may see the whole package, not only one module, for rules such as "exported but never used". The
  official linter sees one module today, so its `unconstructibleType` reports an exported nominal type that another
  module of the same package gives out, where `@allow` has to cover it.
- A package names its linters in its manifest's `linters` field
  ([Packages and projects](../tour/packages#package-manifests)). Whether a linter's `settings` are checked against a
  record type it exports is [#2746](https://github.com/nanyx-lang/nanyx/issues/2746).

# Code with errors

Linters only look at a module that type-checked. A module with type or name errors is typed all the same, with what
did check ([Deferred errors](../tour/error-handling#deferred-errors)), so linters could run on it. The cost of not doing so is in
the editor: linter diagnostics disappear while the file has a type error, and reappear when it is fixed. A module
that does not parse also needs parser error recovery. Linters that run on such code must not assume every node has
a type: a missing entry means "unknown".

# Open questions

- Should linters run on a module with type errors, as above?
- What do the query functions of the public contract look like, and how is the contract versioned?
