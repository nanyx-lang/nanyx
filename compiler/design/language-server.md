# Language server and editor: workstream

> **Status: outline.** This page frames the language server workstream: what it covers, where it is today and what
> comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked under the
> `lsp` label.

## Summary

The editor experience for Nanyx: the language server built into the `nanyx` binary (`nanyx lsp`) and the VS Code
extension that launches it. The server calls the compiler as a library, so most of what it can offer depends on
what the compiler keeps: exact spans, resolved names and types.

## Scope

| Area | Where |
|------|-------|
| Language server | `compiler/Nanyx.LanguageServer` (C#, OmniSharp LSP) and its tests |
| VS Code extension | `extension/`: finding or downloading `nanyx`, starting the server, commands, settings |
| Syntax highlighting | `extension/syntaxes/` (TextMate grammar) |

## Where it is today

- **Diagnostics** ([#97](https://github.com/nanyx-lang/nanyx/issues/97)). The server does full text sync and
  publishes diagnostics on open and change. Each compiler diagnostic has a code (`NYX0201`), published as `code`,
  and the span of the node it is about, published as the full range (UTF-16 columns, through `SourcePositions`).
  Related locations ("declared here") are published as `relatedInformation`. A diagnostic in an imported module
  carries that module's file: it is published on the document being edited, at its start, naming the file and
  linking to the place there; publishing it on the imported file itself is Plan item 7. A diagnostic about the
  whole compilation is shown at the start of the file. Inference still stops at the first error in a module, so
  only one type error shows at a time. See [The language server](../README.md#the-language-server).
- **Hover shows inferred types** ([#120](https://github.com/nanyx-lang/nanyx/issues/120)). Inference records the
  resolved type of every expression by `NodeId` (`TypedModule.ExprTypes`, the `Types` table of the planned
  [semantic model](analyzers.md#3-the-semantic-model)), and the callee's type at each call (`CalleeTypes`), for
  the root module of a compile. Hover shows the narrowest typed expression under the pointer, or a call's
  function at that call site. On a declaration's own name (a def, a parameter, a pattern variable) it shows the
  type of what that declares, recorded by symbol (`SymbolTypes`). A desugared workflow bind or return gets a node id of its own, so the bind as
  written has no type rather than that of the rest of the workflow. Each document
  is compiled once per change and the result cached in `DocumentStore`, shared by diagnostics and hover. A
  declared signature or doc comment needs name resolution ([#98](https://github.com/nanyx-lang/nanyx/issues/98)),
  and a file that has never type-checked shows no hover.
- **Hover on a type's name shows its definition** ([#297](https://github.com/nanyx-lang/nanyx/issues/297)):
  `type Point = (x: int, y: int)`, or `context Logger = (...)` for a context. The type is the one go-to-definition
  finds (`Definition.typeDefinitionAt`), in an annotation, a type definition or a signature's contexts, whether the
  module's own, an imported one or the prelude's, printed from its `TypeDefInfo` with the type printer hover uses.
  A type's parameters keep the names they are written with (`type Box(a) = (item: a)`). On a type's name this
  takes precedence over the type of an expression around it, such as the block a local def's annotation is in.
- **The last good compile is kept** ([#210](https://github.com/nanyx-lang/nanyx/issues/210)). While the text does
  not type-check, hover, lenses and inlay hints read the last text of the document that did (`OpenDocument.LastGood`).
  The client sends whole documents, so what changed since is taken as one region, between the texts' common start
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
  the `α` in its def's lens (see `NyxCompiler.TypeHints`). Like hover, they come from the last good compile, so
  while the file is broken only the def being edited loses its lens.
- **Completion of the names in scope** ([#212](https://github.com/nanyx-lang/nanyx/issues/212)), with their
  types, and the keywords. Inference records where each name can be written (`TypedModule.Scopes`,
  [#211](https://github.com/nanyx-lang/nanyx/issues/211)): a local from its def to the end of its block, a
  parameter to the end of its lambda, a pattern variable to the end of its arm, and a top-level def, an import's
  alias and a name an import or the prelude brings in throughout the module.
  `NyxCompiler.Completion.namesInScope` gives the one symbol each name means at an offset, the nearest declaration
  winning. The handler reads the last good compile at the place nearest the cursor
  (`OpenDocument.NearestInLastGood`): a cursor in text edited since moves to where the edit starts, so a name
  declared since that compile is not offered until the text type-checks again. Whether the cursor is in a comment
  or a string, after a dot, or naming a new def is read from the characters alone (`CompletionContext`), since the
  text being typed rarely parses.
- **Completion after a dot** ([#213](https://github.com/nanyx-lang/nanyx/issues/213)), triggered by typing it.
  The names before the dot are read from the characters (`CompletionContext.MemberPath`). If the first is a value
  in scope, the fields of its record type are offered, followed through the rest of the path. Otherwise the names
  written under the path as a prefix are (`TypedModule.ModuleNames`, every name the module can write, dotted or
  not): an import's exports under its alias, a dotted export a segment at a time (`t.` offers `String`), and the
  definitions attached to a type (`Result.`). An imported definition's type is its module's (`ImportedTypes`). Only a
  name or a dotted name before the dot is understood, not a call's result. A dot on a value never reaches an
  attached definition, so none is offered there; a string's or a list's built-in `length` will be, once the
  compiler has it ([#185](https://github.com/nanyx-lang/nanyx/issues/185)). A value's attached definitions are
  reached with a pipe, and offered after `\`.
- **Completion after a pipe's `\`** ([#269](https://github.com/nanyx-lang/nanyx/issues/269)), triggered by typing
  it. The names just before the `\` are read from the characters (`CompletionContext.PipedPath`), across a line
  break for a pipe that starts a line; a call's result or a literal before it gives no name. If they name a value of
  known type, the definitions attached to that type come first, under each name a pipe resolves it by
  (`Infer.attachedTypeNames`: `String` for a string, `List` for a list, a nominal or alias type's own name), from
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
  published, with a diagnostic's first fix preferred. Only analyzers offer fixes so far (`default/unusedBindings`
  removes an unused local or renames an unused name to `_`); a compiler error's fixes will appear the same way
  once it has them. A fix that edits another file is not offered.
- **A parse error hides the rest of the file**, because the parser has no error recovery.
- **The extension** highlights syntax, finds `nanyx` (setting, `PATH`, or a downloaded release) and offers
  *Download the compiler* and *Restart the language server*.

## Goals

- Errors appear as you type, at the right range, in the right file.
- Hover shows the inferred type; go-to-definition and find-references work for every name.
- Completion offers names in scope and the members of a record or imported module.
- Fixes from structured diagnostics ([#97](https://github.com/nanyx-lang/nanyx/issues/97)) appear as code actions.

## Plan

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
   compile, is kept ([#210](https://github.com/nanyx-lang/nanyx/issues/210)); each symbol's scope is recorded
   ([#211](https://github.com/nanyx-lang/nanyx/issues/211)); the names in scope are offered
   ([#212](https://github.com/nanyx-lang/nanyx/issues/212)); record fields and module exports after a dot are
   offered ([#213](https://github.com/nanyx-lang/nanyx/issues/213)), and import paths
   ([#214](https://github.com/nanyx-lang/nanyx/issues/214)), and a value's attached definitions after a pipe's `\`
   ([#269](https://github.com/nanyx-lang/nanyx/issues/269)).
6. **Code actions.** Done for diagnostic fixes ([#284](https://github.com/nanyx-lang/nanyx/issues/284)); compiler
   errors have no fixes yet.
7. **Diagnostics in imported files.** Publish diagnostics for the file they belong to, not the file being edited.

## Open questions

- Incremental re-checking: is compiling the whole document on every change fast enough as programs grow?
- Should semantic tokens replace or supplement the TextMate grammar?
- Editors beyond VS Code: is `nanyx lsp` plus documented client setup enough?
