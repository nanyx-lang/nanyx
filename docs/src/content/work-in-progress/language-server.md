---
title: "Language server and editor"
description: "The language server built into nanyx, and the VS Code extension"
order: 30
---

> **Status: outline.** This page frames the language server workstream: what it covers, where it is today and what
> comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked under the
> `lsp` label.

# Summary

The editor experience for Nanyx: the language server built into the `nanyx` binary (`nanyx lsp`) and the VS Code
extension that launches it. The server calls the compiler as a library, so most of what it can offer depends on
what the compiler keeps: exact spans, resolved names and types.

# Scope

| Area | Where |
|------|-------|
| Language server | `compiler/Nanyx.LanguageServer` (C#, OmniSharp LSP) and its tests |
| VS Code extension | `extension/`: finding or downloading `nanyx`, starting the server, commands, settings |
| Syntax highlighting | `extension/syntaxes/` (TextMate grammar) |

# Where it is today

- **Diagnostics** ([#97](https://github.com/nanyx-lang/nanyx/issues/97)). The server does full text sync and
  publishes diagnostics on open and change. Each compiler diagnostic has a code (`NYX0201`), published as `code`,
  and the span of the node it is about, published as the full range (UTF-16 columns, through `SourcePositions`).
  Related locations ("declared here") are published as `relatedInformation`. A diagnostic in an imported module
  carries that module's file: it is published on the document being edited, at its start, naming the file and
  linking to the place there; publishing it on the imported file itself is Plan item 7. A diagnostic about the
  whole compilation is shown at the start of the file. Inference still stops at the first error in a module, so
  only one type error shows at a time. See [How the server is built](#how-the-server-is-built).
- **Hover shows inferred types** ([#120](https://github.com/nanyx-lang/nanyx/issues/120)). Inference records the
  resolved type of every expression by `NodeId` (`TypedModule.ExprTypes`, the `Types` table of the planned
  [semantic model](./linters#3-the-semantic-model)), and the callee's type at each call (`CalleeTypes`), for
  the root module of a compile. Hover shows the narrowest typed expression under the pointer, or a call's
  function at that call site. On a declaration's own name (a def, a parameter, a pattern variable) it shows the
  type of what that declares, recorded by symbol (`SymbolTypes`). On a name, at its declaration or where it is
  used, the type comes after the name, as the declaration would write it
  ([#765](https://github.com/nanyx-lang/nanyx/issues/765)): `def takeOrder: [Console] string, list(string) -> ()`
  for a top-level def, `half: int` for a local def, a parameter or a pattern variable. A top-level def of the
  module's own shows the contexts it requires, written or inferred (`TypedModule.ContextTypes`, filled in by
  `RequiredContexts`), instantiated where it is called as its type is (`[Show(Point)] Point -> string`); an
  imported def does not show its contexts yet. A desugared workflow bind or return gets a node id of its own, so the bind as
  written has no type rather than that of the rest of the workflow. Each document
  is compiled once per change and the result cached in `DocumentStore`, shared by diagnostics and hover. A
  declared signature or doc comment needs name resolution ([#98](https://github.com/nanyx-lang/nanyx/issues/98)),
  and a file that has never parsed shows no hover.
- **Hover on a type's name shows its definition** ([#297](https://github.com/nanyx-lang/nanyx/issues/297)):
  `type Point = (x: int, y: int)`, or `context Logger = (...)` for a context. The type is the one go-to-definition
  finds (`Definition.typeDefinitionAt`), in an annotation, a type definition or a signature's contexts, whether the
  module's own, an imported one or the prelude's, printed from its `TypeDefInfo` with the type printer hover uses.
  A type's parameters keep the names they are written with (`type Box(a) = (item: a)`). On a type's name this
  takes precedence over the type of an expression around it, such as the block a local def's annotation is in.
- **Hover on a `_` in a type shows what it was inferred to be**: `int` on the `_` of `def xs: list(_) = [1, 2]`.
  Inference records each hole's type in `ExprTypes` under the type expression's node, and `Hover.typeAt` looks for
  holes among the type expressions at the position (`Hover.typeExprsAt`).
- **Hover and completion show doc comments** ([#719](https://github.com/nanyx-lang/nanyx/issues/719)). Below the
  type, hover shows the doc comment of the symbol `Definition.symbolAt` finds there, and a completion item for a
  name, an attached definition or a pipe target has it as its documentation. `Documentation.Reader` reads it from
  the file the symbol is declared in (the document's own doc comments are parsed once per text, by
  `OpenDocument.DocComments`; another file's, or a standard-library module's, are kept by file), and writes each
  `` [`name`] `` as a link to where the name is declared (`SourceLocations.LinkTo`). Go-to-definition on such a link
  goes there too.
- **A text with type or name errors is typed all the same.** The type checker recovers from them (see
  [deferred errors](./deferred-errors)): what checks has its type, and what does not has the error type, shown as
  `???`. So hover, lenses, inlay hints and completion read the current text whenever it parses
  (`OpenDocument.Typed`), errors or not.
- **The last good compile is kept** ([#210](https://github.com/nanyx-lang/nanyx/issues/210)). While the text does
  not parse (or its imports do not check), hover, lenses, inlay hints and completion read the last text of the
  document that was typed (`OpenDocument.LastGood`). The client sends whole documents, so what changed since is taken as one region, between the texts' common start
  and common end (`EditedRegion`); positions are mapped across it, and a top-level item that overlaps it gets
  nothing, since what that compile says about it may no longer be true. Diagnostics always come from the current
  text.
- **Go-to-definition** for values, parameters, pattern variables and imports, from the symbols and references
  name resolution records ([#98](https://github.com/nanyx-lang/nanyx/issues/98)). A declaration's own name goes
  to itself. A definition in an imported source file opens that file. The standard library is embedded in the
  compiler, so a definition in it opens a read-only copy of its module's source, which the server writes under the
  user's local application data by compiler version (`StandardLibrarySource`). A type name in a type (an
  annotation, a type definition, a context) goes to the type's definition: each type definition and context is a
  `TypeDefinition` symbol, declared at its name, and each type expression that names one records a reference by its
  node. A context member goes to where its context declares it: each member is a `ContextMember` symbol, bound in
  the environment of a def that requires the context, and a qualified `Context(T).member` refers to it too.
- **Inferred types in lenses and inlay hints**, as F# tooling shows them. A top-level def that does not write
  its type gets a lens above it with its whole type; a local def, lambda parameter or pattern variable gets an
  inlay hint after its name (`: int`). The types are the declarations' `SymbolTypes`, less those inference
  records as writing their own (`AnnotatedSymbols`), and the types in one top-level def are named together, so a parameter's `α` is
  the `α` in its def's lens (see `NyxCompiler.TypeHints`). The lens shows the parameterised contexts the def was
  inferred to require before its type (`[Show(α)] α -> string`, from `TypedModule.InferredContextTypes`), as hover
  on the def's name does. A def that writes only its contexts
  (`def greet: [Log] = ...`) has the rest of its annotation inferred, so its inlay hint goes after the contexts,
  without a colon (`[Log] string -> ()`), rather than after the name. Each hint carries its declaration's
  `SymbolKind`, so the server's settings can turn off the hints for local defs, parameters or pattern variables
  one kind at a time. Like hover, they come from the current text
  when it parses, with `???` where an error left a type unknown (`α -> ???`), and from the last good compile while
  it does not, so then only the def being edited loses its lens.
- **Completion of the names in scope** ([#212](https://github.com/nanyx-lang/nanyx/issues/212)), with their
  types, and the keywords. Inference records where each name can be written (`TypedModule.Scopes`,
  [#211](https://github.com/nanyx-lang/nanyx/issues/211)): a local from its def to the end of its block, a
  parameter to the end of its lambda, a pattern variable to the end of its arm, and a top-level def, an import's
  alias and a name an import or the prelude brings in throughout the module.
  `NyxCompiler.Completion.namesInScope` gives the one symbol each name means at an offset, the nearest declaration
  winning. The handler reads the last good compile at the place nearest the cursor
  (`OpenDocument.NearestInLastGood`): a cursor in text edited since moves to where the edit starts, so a name
  declared since that compile is not offered until the text parses again. Whether the cursor is in a comment
  or a string, after a dot, or naming a new def is read from the characters alone (`CompletionContext`), since the
  text being typed rarely parses.
- **Completion after a dot** ([#213](https://github.com/nanyx-lang/nanyx/issues/213)), triggered by typing it.
  The names before the dot are read from the characters (`CompletionContext.MemberPath`). If the first is a value
  in scope, the fields of its record type are offered, followed through the rest of the path. Otherwise the names
  written under the path as a prefix are (`TypedModule.ModuleNames`, every name the module can write, dotted or
  not): an import's exports under its alias, a dotted export a segment at a time (`t.` offers `string`), and the
  definitions attached to a type (`Result.`). An imported definition's type is its module's (`ImportedTypes`). Only a
  name or a dotted name before the dot is understood, not a call's result. A dot on a value never reaches an
  attached definition, so none is offered there; a string's or a list's built-in `length` is
  ([#185](https://github.com/nanyx-lang/nanyx/issues/185); `Infer.builtInProperty`). A value's attached definitions are
  reached with a pipe, and offered after `\`.
- **Completion after a pipe's `\`** ([#269](https://github.com/nanyx-lang/nanyx/issues/269)), triggered by typing
  it. The names just before the `\` are read from the characters (`CompletionContext.PipedPath`), across a line
  break for a pipe that starts a line; a call's result or a literal before it gives no name. If they name a value of
  known type, the definitions attached to that type come first, under each name a pipe resolves it by
  (`Infer.attachedTypeNames`: `string` for a string, `list` for a list, a nominal or alias type's own name), from
  `ModuleNames`; then the functions in scope, which a pipe calls when no attached definition has the name
  (`Completion.pipeTargetsAt`). Only the names just before the `\` are read, though a pipe takes all of the
  expression before it, so in `a + b \f` the type is `b`'s.
- **Completion of import paths** ([#214](https://github.com/nanyx-lang/nanyx/issues/214)), triggered by `/` and
  `"` too, and needing no compile: which import item the cursor is on is read from the characters
  (`CompletionContext.ImportPathAt`), on an `import` line or in an `import ( ... )` block. A module path offers
  `nanyx/`, then the standard library's modules (`StandardLibrary.moduleNames`) a level at a time, each with the
  summary from the comment that opens it (`StandardLibrary.trySummary`). A quoted path offers the `.nyx` files and
  the folders beside the document, or in the folder the path has reached; an untitled document has none. Each item
  replaces only the part after the path's last separator, and a folder or a level asks for the next part at once.
- **Find-references in the open document** ([#276](https://github.com/nanyx-lang/nanyx/issues/276)). From the
  name under the cursor it finds the symbol as go-to-definition does, then every name in the document's compile
  whose node `References` maps to it (`NyxCompiler.Definition.referencesTo`): a call's or pipe's function name, an
  identifier, a qualified context member's name, a type name. An import's references are the alias each name it
  brought in is written with, from `ImportReferences`. The declaration comes first when the client asks for it,
  in whichever file it is. The files that import the document are not searched: that needs an index of the
  workspace.
- **Document symbols** ([#278](https://github.com/nanyx-lang/nanyx/issues/278)), for the outline view, breadcrumbs
  and Go to Symbol in Editor. `NyxCompiler.Outline.ofSource` parses the text and lists its top-level defs (a
  function when the value is a lambda), types (a record, a union or another alias), contexts and `module name =`
  blocks, each with what is declared inside it: a def's local defs, a record's fields, a context's members, a
  block's definitions. The parser lifts a block's defs to the top level, so they are put back by where the block
  is written. Names and their spans come from the declared names the parser records (`DefValue`, `TypeDefBody`,
  `RecordTypeFields`). It needs a parse, not a type-check, so a type error leaves the outline as it was; a text
  that does not parse shows the last good text's outline, less the items edited since.
- **Quick fixes** ([#284](https://github.com/nanyx-lang/nanyx/issues/284)). Each fix of a diagnostic in the
  requested range is a `quickfix` code action: its edits applied to the document, carrying the diagnostic as
  published, with a diagnostic's first fix preferred. Linters offer fixes (`default/unusedBindings` removes an
  unused local or renames an unused name to `_`), and so does one compiler error so far
  ([#497](https://github.com/nanyx-lang/nanyx/issues/497)): an unknown name (`NYX0101`, `NYX0102`) that a
  standard-library module exports offers the import that brings it in, one fix per module that exports it.
  `string.trim(s)` offers `import nanyx/text`; a name qualified by a module's name, as `math.abs(x)`, offers
  `import nanyx/math as math`. The import goes on the line after the file's last import, else after its `module`
  line, else after the `::` directives (which must come before any code), else at the top. `NyxCompiler.ImportFixes` attaches these fixes in the compiler, from an index of the
  library's exports built once; the prelude is left out, since its names are always in scope. A fix that edits
  another file is not offered.
- **A parse error hides the rest of the file**, because the parser has no error recovery.
- **The extension** highlights syntax, finds `nanyx` (setting, `PATH`, or a downloaded release) and offers
  *Download the compiler* and *Restart the language server*. Its settings are described for users on the
  [VS Code extension](../standard-tools/vs-code-extension) page, which a change to the extension's settings or
  commands updates in the same PR.

## How the server is built

`nanyx lsp` runs a [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) server over
standard input and output, so the same binary that compiles programs also serves editors. The VS Code extension in
`extension/` launches it; any other LSP client can start it with `nanyx lsp` and `TransportKind.stdio`.

The server lives in `Nanyx.LanguageServer` and calls the compiler as a library: on every open and change it runs
`Compiler.compileSource` (or `Compiler.compile` for an untitled buffer) on the editor's text, keeps the
`CompileResult` in `DocumentStore`, and publishes the resulting diagnostics; hover, go-to-definition, lenses and
inlay hints read the same result, so each change is compiled once. Standard-library imports resolve because the
library is embedded in the compiler, and each of its modules is checked once for the life of the server, not on
every change (see [Standard library implementation](./standard-library-implementation)); imports by path resolve relative to the file as they do for `nanyx check`. A
compiler exception is reported as a diagnostic rather than taking the server down.

Each feature is one handler class in `Nanyx.LanguageServer/Handlers/`, registered in
`NanyxLanguageServer.Configure`. Hover and the type hints name made-up type variables `α`, `β`, … through the Greek
alphabet, a union's rest variable from the other end (`#some(α) | ω`), and a record's rest spread with an ellipsis,
written bare where it appears nowhere else (`list(name: α, …) -> list(α)`); annotation variables keep their names, and
a type alias is shown by its name (`Customer`, not `(name: string)`: inference carries aliases as `TyAlias`).
`nanyx lsp --ascii` writes types in ASCII only. `--top-level-type-hints inlay` shows the top-level defs' types as
inlay hints after their names instead of lenses, and `--top-level-type-hints off` shows neither (the default is
`lens`). `--no-local-type-hints`, `--no-parameter-type-hints` and `--no-pattern-variable-type-hints` turn off the
inlay hints for each of the other kinds of declaration.

The tests in `Nanyx.LanguageServer.Tests` start the server in-process and talk to it through OmniSharp's LSP client
over a pair of pipes, so they exercise the real wire protocol.

# Goals

- Errors appear as you type, at the right range, in the right file.
- Hover shows the inferred type; go-to-definition and find-references work for every name.
- Completion offers names in scope and the members of a record or imported module.
- Fixes from structured diagnostics ([#97](https://github.com/nanyx-lang/nanyx/issues/97)) appear as code actions.

# Plan

_To do._ Candidate items, in rough dependency order; those not linked to an issue are not yet filed:

1. **Exact ranges.** Done ([#97](https://github.com/nanyx-lang/nanyx/issues/97)): diagnostics carry spans, codes
   and related locations, and are published as LSP ranges, `code` and `relatedInformation`. What is left waits on
   [#96](https://github.com/nanyx-lang/nanyx/issues/96): a diagnostic about a field name, a type annotation or an
   attribute is reported at the nearest node that has a span (the field's value, the definition's name, the item
   after the attribute).
2. **Hover.** Inferred types are done ([#120](https://github.com/nanyx-lang/nanyx/issues/120)); declared
   signatures and docs follow from name resolution ([#98](https://github.com/nanyx-lang/nanyx/issues/98)).
3. **Go-to-definition and find-references.** From the recorded symbols and references ([#98](https://github.com/nanyx-lang/nanyx/issues/98)).
   Go-to-definition is done for values (including those in the standard library), type names and context members
   ([#266](https://github.com/nanyx-lang/nanyx/issues/266)). Find-references is done in the open document
   ([#276](https://github.com/nanyx-lang/nanyx/issues/276)); across the workspace it is not.
4. **Document symbols.** Done ([#278](https://github.com/nanyx-lang/nanyx/issues/278)). Workspace symbols (Go to
   Symbol in Workspace) would need an index of the workspace's files, as find-references across it does.
5. **Completion.** The last good compile, which completion needs because it is asked for while the text does not
   parse, is kept ([#210](https://github.com/nanyx-lang/nanyx/issues/210)); each symbol's scope is recorded
   ([#211](https://github.com/nanyx-lang/nanyx/issues/211)); the names in scope are offered
   ([#212](https://github.com/nanyx-lang/nanyx/issues/212)); record fields and module exports after a dot are
   offered ([#213](https://github.com/nanyx-lang/nanyx/issues/213)), and import paths
   ([#214](https://github.com/nanyx-lang/nanyx/issues/214)), and a value's attached definitions after a pipe's `\`
   ([#269](https://github.com/nanyx-lang/nanyx/issues/269)).
6. **Code actions.** Done for diagnostic fixes ([#284](https://github.com/nanyx-lang/nanyx/issues/284)). The first
   compiler error with fixes is an unknown name the standard library exports, which offers its import
   ([#497](https://github.com/nanyx-lang/nanyx/issues/497)); other compiler errors have none yet.
7. **Diagnostics in imported files.** Publish diagnostics for the file they belong to, not the file being edited.
8. **Doc comments, a library browser, search by type and an info view.** Planned on
   [Info view and library browser](./info-view-and-library-browser).

# Open questions

- Incremental re-checking: is compiling the whole document on every change fast enough as programs grow?
- Should semantic tokens replace or supplement the TextMate grammar?
- Editors beyond VS Code: is `nanyx lsp` plus documented client setup enough?
