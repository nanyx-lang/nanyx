# Linters: compiler design

> **Status: draft.** This is the compiler-side design for linters. The user-facing model is described in
> [Linters](../../docs/src/content/advanced/linters.md) and
> [Official style linter](../../docs/src/content/standard-tools/official-linter.md); this document is about
> what the compiler has to provide so that model can be built without reworking the front end later.

## Summary

Linters inspect a checked module and report diagnostics that are not compile errors: unused bindings,
unreachable branches, style rules. The docs promise that they are written in Nanyx, configured per project, and
show up in the editor next to compiler errors.

Most of what linters need is not a linter API. It is the substrate under it: exact source spans, a
structured diagnostic, and a semantic model that relates every name to its declaration. Those are cheap to add
while the compiler is small and expensive to add later, because every phase and every test touches them. So the
plan is to build the substrate first, prove it with a handful of linters written in F# inside the compiler, and
only then design the public contract that Nanyx-authored linters will be written against.

## Goals

- Linter diagnostics are positioned exactly (start and end), in the right file, and can carry fixes.
- Every diagnostic, compiler or linter, has a stable code, so projects can configure, promote or suppress it.
- Linters see the program as the user wrote it, with types and resolved names.
- A linter can never break compilation: a crashing linter produces a diagnostic, not a failed build.
- The compiler itself emits only errors (see [No warnings](../../docs/src/content/foundations/principles.md)).

## Non-goals (for now)

- The public, versioned API for linters written in Nanyx. It is the last step, not the first.
- Running linters on code that does not type-check (see [Code with errors](#code-with-errors)).

## Where the compiler is today

- **Every syntax node has a span** ([#96](https://github.com/nanyx-lang/nanyx/issues/96)). Every top-level item,
  expression, pattern, statement, type expression and import has a `SourceSpan`, and all but imports have a
  `NodeId`. The names a declaration declares are not nodes, so their spans are kept beside the tree (below).
- **Diagnostics are structured** ([#97](https://github.com/nanyx-lang/nanyx/issues/97)). `Diagnostic` is the
  record in [section 2](#2-structured-diagnostics), every compiler error has a code from the table in
  `Diagnostics.fs`, and each is created with the span of the node it is about, in that node's file, so a
  diagnostic in an imported module is reported there. Type constraints carry the span of the expression or
  pattern that made them, which is where unification reports a failure. An attribute's errors are reported at the attribute.
  A diagnostic about something that has no node of its own (a field name, a declared name) is reported at the
  nearest node, and one about an annotation still is too, though annotations now have spans of their own.
  The compiler emits only `Critical` and `Error`.
- **The type checker sees desugared code.** `desugar` turns workflow binds and `return` into calls to the
  keyword function and `pure` before inference, and `applyAttributes` replaces `@entry` with a synthesized call.
  The synthesized nodes have no position and no link back to the syntax they came from.
- **Name resolution is recorded** ([#98](https://github.com/nanyx-lang/nanyx/issues/98)). Each binding in
  `TypeEnv` carries the symbol it declares, so inference records which symbol every identifier, call, pipe target
  and qualified name resolved to (`TypedModule.Symbols`, `References`, and `ImportReferences` for the import a
  name came through). Values, parameters, pattern variables and imports are symbols; type names and context
  members are not yet, though type expressions now have nodes to record them against. A declaration is a node (a def, a
  parameter list, a pattern), but the names it declares are not, so the parser keeps their spans beside the tree
  (`DeclaredNames`), keyed by the node that declares them.
- **The typed tree is a placeholder.** `TypedExpr` wraps the untyped `Expression` with a type and some optional
  children, and is turned back into syntax (`moduleFromTypedItems`) for the backend.

## Design

### 1. Spans on every node

Every syntax node carries a `SourceSpan`:

```fsharp
type SourceSpan =
    { File: string       // the path the module was parsed from ("" for in-memory source)
      Start: int         // character offset, inclusive
      End: int           // character offset, exclusive; trailing whitespace is not included
      StartLine: int     // zero-based
      StartColumn: int } // zero-based
```

Offsets make slicing and text edits trivial, and they are what FParsec's `Position.Index` gives. The start's line
and column are kept as well, so code without the source text (inference, diagnostics) can report a position; the
end's line and column come from the source when needed. Offsets are into the source with line endings normalised
to `\n`.

The AST moves from "some cases have an optional range" to "every node has a span". The least invasive shape is a
wrapper record around the existing unions:

```fsharp
type Expression = { Kind: ExpressionKind; Span: SourceSpan; Id: NodeId }
and ExpressionKind =
    | LiteralExpr of Literal
    | IdentifierExpr of Identifier
    ...
```

and the same for `Pattern`, `Statement`, `TypeExpr` and top-level items. This is one mechanical refactor of the
parser, inference and the WASM backend; it can be done one node family at a time (expressions first, since that
is where most diagnostics land). The `SourceRange option` fields on individual cases go away.

`NodeId` is an integer, unique within the process, assigned when a node is created. It is what side tables (types, resolved symbols)
are keyed by, so analysis results do not need a parallel copy of the tree.

A diagnostic is created with the span of the node it is about (done in
[#97](https://github.com/nanyx-lang/nanyx/issues/97), which removed the positioning of diagnostics after the fact).
Once every node has a span, the diagnostics that are now reported at the nearest enclosing node move to their own.

### 2. Structured diagnostics

> **Done** in [#97](https://github.com/nanyx-lang/nanyx/issues/97), except that no compiler error offers
> suggestions or fixes yet. The language server offers fixes as quick fixes
> ([#284](https://github.com/nanyx-lang/nanyx/issues/284)).

The diagnostic follows the error anatomy in [Principles](../../docs/src/content/foundations/principles.md#human-readable-errors):
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

Introducing codes for existing errors can be incremental: give every current error a code when its constructor
is changed to take a span.

### 3. The semantic model

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
- **Desugaring keeps provenance.** Every node `desugar` and `applyAttributes` synthesize gets the span (and
  originating `NodeId`) of the syntax it replaced, and `Lowered` records the mapping. Linters walk `Syntax`; the
  type of a workflow bind is found through `Lowered`. This is much cheaper than teaching inference to check
  surface syntax directly, and it can be replaced by that later without changing what linters see.
- **Import inlining stays where it is.** `CheckedModule` already separates the module's own items from the ones
  inlined for the backend; linters read the semantic model of the module's own items, so inlining does not hide
  anything once references to imports are recorded.

Whether `TypedExpr` survives alongside the side tables, or is replaced by them, is an implementation choice for
the first issue that touches it. Side tables keyed by `NodeId` avoid keeping two trees in step.

### 4. The linter host (internal)

> **Built** in [#100](https://github.com/nanyx-lang/nanyx/issues/100) (`Linters.fs`). The official `default`
> linter's first rule is `unusedBindings` ([#101](https://github.com/nanyx-lang/nanyx/issues/101)), whose fixes are
> the first any diagnostic offers; the language server shows them as quick fixes. Its second is `nonTailRecursion`
> ([tail-calls.md](tail-calls.md#checking-tail-position)), and its third `unreachableArms`
> ([#103](https://github.com/nanyx-lang/nanyx/issues/103)), which asks `PatternSpace` which earlier arms cover an arm:
> the module the exhaustiveness check in `Infer.fs` uses too. The semantic model
> carries the module's text, which fixes need (to remove a whole line, say). What the host settled:
>
> - Only the module being compiled is analyzed. An imported module is analyzed when it is compiled itself, so its
>   diagnostics are not repeated in every importer (the language server would show them at the top of each one).
> - A failing linter is reported under a rule every linter has, `<linter>/linterFailed`, a warning by
>   default, so it can be configured or turned off like any rule. Throwing, or reporting a rule the linter does
>   not declare, both count as failing, and the linter's other reports for the module are left out.
> - The `SemanticModel` has no `Lowered` table yet ([#99](https://github.com/nanyx-lang/nanyx/issues/99)).
> - The configuration is `LinterOptions` (the linters to run, and severities by code), passed to
>   `Compiler.compileWith` and its file and source variants. A package's `.nyxpack` sets it with an
>   `[linters.<name>]` table per linter to run ([#279](https://github.com/nanyx-lang/nanyx/issues/279)), read by
>   `nanyx build`, `nanyx run` and the language server; without the table, the linter does not run. A file
>   outside any package turns linters on with `::linter` directives in the same way, and without them runs
>   none. Linters are opt-in everywhere, including `Compiler.compile` and its variants.

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

Configuration is a compiler option (`Map<string, Severity option>` keyed by code) until the package manifest
exists; then the manifest is parsed into the same value. A rule promoted to `error` fails `nanyx run`/build like a
compiler error does.

The first linter is the official one from the docs, built rule by rule. The first rules are chosen to exercise
different parts of the model:

| Rule | Needs |
|------|-------|
| Unused local, argument and pattern variable | symbols and references |
| Unused import | import symbols, qualified references |
| Unreachable match branch | types and the pattern-space code behind the exhaustiveness check |
| Exported definition without an annotation | the surface tree (annotations as written) |
| Unused return value | types of expression statements |

### 5. The public contract (later)

Once the internal linters have shaken out the semantic model, the public contract can be designed with
evidence rather than guesses. The direction so far:

- Linters are Nanyx packages, compiled to WebAssembly and run at compile time on the embedded Wasmtime engine
  the compiler already ships.
- They see the semantic model through query functions (type of a node, symbol of a reference, references to a
  symbol, children of a node) rather than a serialized copy of the whole tree. Queries keep the compiler's internal
  representation free to change, and avoid copying large trees across the WASM boundary.
- The contract is versioned from day one, and a linter declares the version it targets.

## Code with errors

Today any error stops compilation before a typed module exists, so linters can only run on modules with no
errors. That is the right first step and keeps linters simple. Its cost is in the editor: linter diagnostics
disappear while the file has a type error, and reappear when it is fixed.

Running linters on code with errors needs parser error recovery and an inference that keeps going after an
error, which the language server wants for its own reasons. It is out of scope here, but the semantic model
should not assume every node has a type: `Types` is a map, and a missing entry means "unknown".

## Plan

1. **Foundations.** Spans on every node; structured diagnostics with codes; diagnostics carry their file.
2. **Semantic model.** Record name resolution as symbols and references; keep desugaring provenance.
3. **Host and first rules.** The internal linter host with configuration through compiler options, then the
   rules in the table above, one at a time.
4. **Public linters.** Design the contract for Nanyx-authored linters, based on what steps 1 to 3 taught us.

Steps 1 and 2 are useful on their own: the language server needs exact ranges and symbols for hover and
go-to-definition regardless of linters.

## Open questions

- **Suppression in source.** An attribute (`@allow(default/unusedBindings)`) on a definition, a comment form, or
  only the convention that a `_`-prefixed name is intentionally unused? The docs already use `_` patterns.
- ~~**Compiler error codes.** One flat `NYX0001` sequence, or ranges per phase?~~ Ranges per phase (see
  [section 2](#2-structured-diagnostics)).
- **Where linters run in the CLI.** Always, or only for `check` and build, with `run` skipping them for speed?
- **Linters across modules.** "Exported but never used anywhere in the project" needs every module's semantic
  model at once; per-module analysis is enough for the first rules.
- **Fix application.** Fixes are text edits; does the CLI get a `--fix`, or are fixes editor-only at first?
