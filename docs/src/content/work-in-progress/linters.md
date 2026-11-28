---
title: "Linters: compiler design"
description: "The public contract for linters written in Nanyx, and moving the official linter into a package"
order: 29
---

How linters are configured and what they report is described in [Linters](../advanced/linters), and the official
linter's rules, `@allow` and what happens when the linter fails in
[Official style linter](../standard-tools/official-linter). Today the official linter is F#, compiled into the
compiler: its rules are in `Linters.fs` and the `Linter*.fs` files beside it, and `Library.compileChecked` runs them
over each module that type-checked. This page is the design for the contract a linter written in Nanyx is given
([#107](https://github.com/nanyx-lang/nanyx/issues/107)), and for moving the official linter onto it, as the package
`nanyx.lint` ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)). The [open questions](#open-questions) are
the maintainer's to decide.

# What is decided

Decided by the maintainer ([#107](https://github.com/nanyx-lang/nanyx/issues/107),
[#1343](https://github.com/nanyx-lang/nanyx/issues/1343),
[Shipped packages](../deep-dives/shipped-packages#why-the-official-ones-are-ordinary-packages)):

- Linters are Nanyx packages, compiled to WebAssembly and run at compile time on the Wasmtime engine the compiler
  already ships. The official linter is one, with no privileged access to the compiler, so that the contract every
  other linter uses is the one that is tested.
- The contract is versioned from day one, and a linter says which version it targets.
- A fix is a list of text edits, applied by the editor and by `nanyx check --fix`
  ([#3037](https://github.com/nanyx-lang/nanyx/issues/3037)).
- A package names its linters in its manifest's `linters` field, and a script with `::linter` directives, as now.
- The compiler reports errors only. Everything that isn't an error is a linter's.

# The contract

In short: a linter is a function from one checked module to a list of reports. The module is given as its syntax
tree, as written, and the questions only type checking can answer (the type of a node, what a name refers to) are
asked through a context.

## Three packages

| Package | What it is |
|---------|------------|
| `nanyx.analysis` | The contract: the types of types and symbols, of a report and a fix, and the `Analysis` context whose members answer questions about the module. A linter depends on it. The syntax tree's types are in `nanyx.syntax`, which it depends on and which the formatter shares ([Formatter: a Nanyx package](formatter-package#the-tree)). |
| `nanyx.analysis.host` | The linter host. It runs a linter for the compiler: it reads the module the compiler gives it through `nanyx:lint`, an import of the runtime's own, runs the linter's `lint` with `Analysis`, and gives the reports back. |
| `nanyx.lint` | The official linter, written against `nanyx.analysis` like any other. |

All three are shipped packages under `packages/`, at the compiler's version, so an offline install lints a program
with nothing downloaded. They follow the naming of the other shipped packages: a host is `<name>.host`, beside the
library its programs are written against ([Shipped packages](../deep-dives/shipped-packages)). This is the shape the
test runner already has: `nanyx.test.host` runs one test of a test build for the runtime, talking to it through
`nanyx:test` ([Test builds](../deep-dives/test-builds)).

## A linter package

A linter is a library that declares itself a linter in its manifest, with its rules, as a host declares itself with its
root and prelude:

```nanyx fragment
(
  package = (
    name = "acme.lint"
    version = "1.0.0"
    nanyx = "0.53"
    sources = ["src/**/*.nyx"]
    linter = (
      root = "acme.lint"
      name = "acme"
      rules = [
        (
          id = "noTodo"
          title = "TODO comment"
          severity = #info
          description = "A TODO comment left in the code."
        )
      ]
    )
  )
  dependencies = [(package = "nanyx.analysis", version = "0.53.0")]
)
```

The rules are in the manifest so the compiler knows them without running anything: it checks a project's `settings`
and `::lint` directives against them, and `@allow` names them, before any linter is built. `name` is the first part of
its rules' codes, `acme/noTodo`; the official linter's is `default`, so `@allow(default/unusedBindings)` keeps working.
A rule's `unnecessary = true` says what it reports is code that does nothing, which the editor draws faint.

The linter's root module exports `lint`:

```nanyx fragment
export module acme.lint

import (
  nanyx.analysis/analysis as (Analysis, Report)
  nanyx.analysis/syntax as (Module)
)

export def lint: [Analysis] Module -> list(Report) = { m ->
  ...
}
```

## What a linter sees

The module is given as data, its syntax tree as written: before desugaring, with its attributes, so that a rule reports
what the user wrote and a fix edits it. The tree's types are `nanyx.analysis`'s [`syntax`](../stdlib/syntax) module.
Each node is a record of its `id`, its `span` and its `kind`, by whose `id` the `Analysis` context answers questions
about it:

```nanyx fragment
export type Expr = (id: NodeId, span: Span, kind: ExprKind)

export type ExprKind =
  | #literal(Literal)
  | #name(string)
  | #call(name: string, nameSpan: #some(Span) | #none, arguments: list(Expr))
  | #block(list(Statement))
  | #match(values: list(Expr), arms: list(MatchArm))
  | ...
```

The node kinds mirror the parser's (`Ast.fs`): `Module`, `TopLevelItem`, `Statement`, `Expr`, `Pattern`, `TypeExpr`,
`Import`, and the records they hold, which name each other, as types can since
[#3186](https://github.com/nanyx-lang/nanyx/issues/3186). A rule pattern-matches on them, as the F# rules match on
`ExpressionKind` today. Spans are offsets into the module's text, with its line endings made `\n`, as the compiler's
are. Constructs the compiler desugars before checking, such as a `for` loop, are the calls they are desugared to, for
now; giving them nodes of their own, when the rules that read them move, is open.

What type checking found out is asked of the context, by node or by symbol: `nanyx.analysis`'s
[`analysis`](../stdlib/analysis) module, whose page lists its members and the types they answer with, `Ty`, `Symbol` and
`DocComment`.

- `typeOf` is the type of an expression; `calleeTypeOf` the type of the function a call or pipe calls there, with the
  contexts it requires.
- `symbolOf` is the symbol a name, call, pipe target or qualified name refers to, as written; `importOf` the import a
  name came in through. `symbol` gives a symbol's name, kind (a def, a local, a parameter, a type, a context, a member,
  an import...), where it is declared, whether it is exported, and the module it is in.
- `typeOfSymbol` and `contextsOf` are a declaration's type and the contexts it requires, written or inferred;
  `referencesTo` the nodes that refer to it in this module.
- `lowered` is the nodes desugaring made for a node as written, such as a workflow bind's call, whose types `typeOf`
  also has.
- `describe` writes a type as the compiler's messages and hover do, so a linter's message names a type the same way.
- `coveringArms`, `redundantTypeName` and `docComments` are [the compiler's analyses](#the-compilers-analyses-are-queries).

`Ty` is a union a linter can match on: a type variable, a named type with its arguments (nominal or not), a function
with its parameters, the contexts it requires and its result, a record, a tuple, a union of tags (open or closed), and
`undefined`. An answer of `#unknown` means the compiler doesn't know, and a rule must allow for it.

Why the tree is data and the semantics a context (decided by the maintainer): rules match on syntax all the time, which
a tree of handles would turn into a call per node; one module's tree is small, made once per lint, and changes when the
language does, which it would anyway. The side tables (types, symbols, references) are far larger, most of a rule
never looks at, and are the part of the compiler most likely to change shape, so they stay behind queries. This
narrows #107's direction, which had the tree behind queries too.

## The compiler's analyses are queries

Some rules lean on what the compiler works out for its own errors. Each of these is a query on `Analysis`, which every
linter gets, rather than written again in Nanyx, where it would drift from the compiler's (decided by the maintainer):

- `coveringArms: (NodeId, int) -> list(int)`, of a `match`, the arms before an arm that between them match every value
  it does, from the pattern space that exhaustiveness checking uses (`PatternSpace.fs`), for `unreachableArms`;
- `redundantTypeName: NodeId -> bool`, whether a pipe's target is written with a type's name that the name without it
  reaches as well (`InferState.RedundantTypeNames`), for `redundantTypeNames`;
- `docComments: () -> list(DocComment)`, each doc comment with what it documents, as the parser attaches them, for
  `strayDocComments`.

A rule that needs another such answer adds a query in the same way. A purely syntactic walk, such as which calls are in
tail position, is the linter's own.

## What a linter reports

```nanyx fragment
export type Report = (
  rule: string
  span: Span
  message: string
  subject: #some(string) | #none
  related: list((Span, string))
  fixes: list(Fix)
)

export type Fix = (title: string, edits: list(TextEdit))

export type TextEdit = (span: Span, newText: string)
```

A report is what the F# rules' `Report` is today: the rule that found it, by its `id`; where; the message; the one name
a headline shows after the rule's title (`subject`, the binding an `unusedBindings` report is about); related places,
each with what it is; and its fixes. A linter doesn't choose a severity or a code: the host makes the report a
diagnostic with the rule's code, `<name>/<rule>`, and the severity the project configures, or the rule's default.

## How it runs

- **Found as a package.** A `linters` entry names a package at a version, or by `path`, resolved and locked as a
  dependency is, and the lockfile pins it. The official linter is the shipped `nanyx.lint`, seeded with the compiler,
  so it resolves offline. A script's `::linter nanyx.lint` takes the seeded version, as `::install` does.
- **Built once.** The compiler builds a lint build of the linter, its `lint` passed to the linter host's `run` as a
  test build passes its tests, compiles it to WebAssembly, and keeps it in the package cache by the linter's SHA-256 and
  the compiler's version. A shipped linter is built when it is seeded.
- **Run per module.** For each module that type-checked, the host starts an instance of the lint build, as the test
  runner starts one per test, answers the `Analysis` queries as imports from the compiler's side tables, and reads the
  reports back. The compiled module is kept per linter, so a keystroke in the editor costs an instance and a call, not a
  compile.
- **Values cross as data.** The tree, each query's answer, and the reports cross the boundary as one small binary
  format of data: ints, floats, text, booleans, lists, records of named fields and tags with a payload. The host turns
  data into `nanyx.analysis`'s types, and its reports into data, through their type descriptions
  ([Inspecting a type](../tour/functions#inspecting-a-type)), matching fields and tags by name. So the compiler's
  encoder and the package's types agree on names alone, and nothing hand-writes a decoder for each kind of node.
- **The compiler keeps the host.** `Linters.fs` becomes the host: configuration, severities, `@allow`, the reports a
  linter gets wrong, and `linterFailed`. A linter that traps, runs out of fuel, or reports a rule it doesn't declare is
  one `<name>/linterFailed` warning, as today, and the module still compiles.
- **`@allow` is the host's.** Only the host sees every linter's diagnostics, so it is the host, not the official linter,
  that reports an `@allow` that suppresses nothing, or names a linter or rule there is none of. It does so whenever any
  linter runs, under a code of its own, `lint/unusedAllows` in place of today's `default/unusedAllows`, set in the
  manifest apart from any linter's rules (decided by the maintainer).
- **Who runs it.** `nanyx check`, `build`, `run` and `test` lint as they do now, and so does the language server. There
  is no `nanyx lint` command: `nanyx check` is it. The compiler's core (`NanyxCompiler`) doesn't reference the Wasm
  backend or the runtime, so it takes the linters to run as functions, as `LinterOptions` holds them now; the CLI,
  which references both, makes them from the packages, and gives them to the language server it starts.
- **The playground** runs the compiler in the browser, where there is no Wasmtime. It runs a linter's lint build with
  the browser's own WebAssembly engine, answering the `Analysis` imports from the compiler's worker (decided by the
  maintainer). That is a stage of its own, after the F# rules go: the playground runs a pinned release, so it keeps the
  F# rules until a release without them, and lints nothing from then until the stage lands.

## Compiler warnings stay out

The compiler's own diagnostics are errors; the two warnings it reports, `NYX0051` and `NYX0303`, are about the package
index, not code. A check that isn't an error is a linter rule, in `nanyx.lint` or a linter of its own, never a
diagnostic of the compiler's. Where a rule needs something only the compiler knows, the answer is a new query on
`Analysis`, public and versioned with the rest, not a warning, and not a privileged hook for the official linter.

## Versioning

The contract is `nanyx.analysis`'s public API, and its version is the compiler's, as every shipped package's is. A
linter depends on it at the version it targets, and the compiler runs a linter built against its own `nanyx.analysis`
only: one that requires another is reported at its `linters` entry, as a package whose `nanyx` doesn't accept the
compiler is (`NYX0052`). Below 1.0.0 a minor release may change the contract, and the package index's API check, which
already records whether a shipped package's API changed more than its version allows, says when it does.

# Moving the official linter

The F# rules and the package run side by side until the last rule moves, so that each rule moves in a PR of its own
and its tests show it reports what it did:

1. **The contract.** `nanyx.analysis` and `nanyx.analysis.host`, the encoding of the tree and the `Analysis` imports,
   and lint builds ([#3171](https://github.com/nanyx-lang/nanyx/issues/3171)): built. `nanyx.analysis` holds the
   types; `LintData`, `LintSyntax` and `LintAnswers` in the compiler encode the module and answer its questions;
   `nanyx.analysis.host` asks them through the runtime's `nanyx:lint` (`Nanyx.Runtime.WasmLinter`); and
   `Transpiler.Wasm.LintBuild` makes a lint build of a linter written as a script under the linter host. A probe
   linter in the Wasm tests (`RunLintBuildTests.fs`) runs end to end. Left for
   [#3172](https://github.com/nanyx-lang/nanyx/issues/3172): a linter package declaring itself in its manifest,
   building and caching its lint build, and running it from a manifest's `linters` in the CLI and the language server.
2. **The package.** `nanyx.lint` under `packages/`, at the compiler's version, declaring every rule. The host runs it
   as the official linter, and runs the F# rule for each rule the package doesn't implement yet.
3. **The rules**, a group at a time: the `unused*` rules; `unreachableArms` and `unreachableCode`; `unusedResults`,
   `uselessExpressions` and `unawaitedTask`; the style rules (`semicolons`, `strayDocComments`, `useInBraces`,
   `shadowedPreludeNames`); the rules about types (`unannotatedExports`, `resultOnlyTypeVariable`,
   `unconstructibleType`, `noTagInCommon`, `redundantTypeNames`); `repeatedPulls`; `nonTailRecursion`. The tests in
   `Features/Linters` stay, and run the package: same codes, messages, spans and fixes.
   `unusedAllows` moves to the host as `lint/unusedAllows` in this stage too.
4. **The F# rules go.** `Linters.fs` keeps only the host, the `Linter*.fs` rule files are deleted, and a Deep dives
   page describes how linters run.
5. **The playground** runs linter packages with the browser's WebAssembly engine.

# Open questions

These are left as they are today for now, and can be decided after the move:

- **Code with errors.** Linters look only at a module that type-checked, so in the editor a linter's diagnostics
  disappear while the file has a type error. A module with errors is typed all the same, with what did check
  ([Deferred errors](../tour/error-handling#deferred-errors)), so linters could run on it, where a type that didn't check is
  `#unknown`.
- **More than one module.** A linter sees one module at a time, as the official one does today, so
  `unconstructibleType` reports a type another module of the package gives out. A whole-package entry point, for rules
  such as "exported but never used", could come later beside `lint`.
- **Settings.** A linter's `settings` hold only `rules` today. Whether a linter can take settings of its own, checked
  against a record type it exports, is [#2746](https://github.com/nanyx-lang/nanyx/issues/2746).
