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
- **The last good compile is kept** ([#210](https://github.com/nanyx-lang/nanyx/issues/210)). While the text does
  not type-check, hover, lenses and inlay hints read the last text of the document that did (`OpenDocument.LastGood`).
  The client sends whole documents, so what changed since is taken as one region, between the texts' common start
  and common end (`EditedRegion`); positions are mapped across it, and a top-level item that overlaps it gets
  nothing, since what that compile says about it may no longer be true. Diagnostics always come from the current
  text.
- **Go-to-definition** for values, parameters, pattern variables and imports, from the symbols and references
  name resolution records ([#98](https://github.com/nanyx-lang/nanyx/issues/98)). A declaration's own name goes
  to itself. A definition in an imported source file opens that file; one in the standard library goes nowhere,
  since the library is embedded in the compiler, not a file on disk. Type names and context members are not
  symbols yet, so they go nowhere either.
- **Inferred types in lenses and inlay hints**, as F# tooling shows them. A top-level def that does not write
  its type gets a lens above it with its whole type; a local def, lambda parameter or pattern variable gets an
  inlay hint after its name (`: int`). The types are the declarations' `SymbolTypes`, less those inference
  records as writing their own (`AnnotatedSymbols`), and the types in one top-level def are named together, so a parameter's `α` is
  the `α` in its def's lens (see `NyxCompiler.TypeHints`). Like hover, they come from the last good compile, so
  while the file is broken only the def being edited loses its lens.
- **No find-references or completion.** Each is one handler in `Handlers/`. Find-references can read the same
  `References` table, though only of the open document's own compile. For completion, inference records where
  each name can be written (`TypedModule.Scopes`, [#211](https://github.com/nanyx-lang/nanyx/issues/211)): a
  local from its def to the end of its block, a parameter to the end of its lambda, a pattern variable to the end
  of its arm, and a top-level def, an import's alias and a name an import or the prelude brings in throughout the
  module. `NyxCompiler.Completion.namesInScope` gives the one symbol each name means at an offset, the nearest
  declaration winning.
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
   Go-to-definition is done for values; it still needs type names and context members as symbols, and a
   way to open standard-library source (a virtual document, say).
4. **Document symbols.** The outline view, from top-level definitions.
5. **Completion.** The last good compile, which completion needs because it is asked for while the text does not
   compile, is kept ([#210](https://github.com/nanyx-lang/nanyx/issues/210)). Filed: record where each symbol is in scope
   ([#211](https://github.com/nanyx-lang/nanyx/issues/211)); then names in scope
   ([#212](https://github.com/nanyx-lang/nanyx/issues/212)), record fields, module exports and attached definitions
   after a dot ([#213](https://github.com/nanyx-lang/nanyx/issues/213)), and import paths
   ([#214](https://github.com/nanyx-lang/nanyx/issues/214)).
6. **Code actions.** From diagnostic fixes.
7. **Diagnostics in imported files.** Publish diagnostics for the file they belong to, not the file being edited.

## Open questions

- Incremental re-checking: is compiling the whole document on every change fast enough as programs grow?
- Should semantic tokens replace or supplement the TextMate grammar?
- Editors beyond VS Code: is `nanyx lsp` plus documented client setup enough?
