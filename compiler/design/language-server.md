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
  and a file that fails to type-check shows no hover.
- **Go-to-definition** for values, parameters, pattern variables and imports, from the symbols and references
  name resolution records ([#98](https://github.com/nanyx-lang/nanyx/issues/98)). A declaration's own name goes
  to itself. A definition in an imported source file opens that file; one in the standard library goes nowhere,
  since the library is embedded in the compiler, not a file on disk. Type names and context members are not
  symbols yet, so they go nowhere either.
- **Inferred types in lenses and inlay hints**, as F# tooling shows them. A top-level def that does not write
  its type gets a lens above it with its whole type; a local def, lambda parameter or pattern variable gets an
  inlay hint after its name (`: int`). The types are the declarations' `SymbolTypes`, less those inference
  records as writing their own (`AnnotatedSymbols`), and the types in one top-level def are named together, so a parameter's `α` is
  the `α` in its def's lens (see `NyxCompiler.TypeHints`). Like hover, a file that fails to type-check shows none,
  so they disappear while the file is broken and the code moves up and down as the lenses come and go; keeping
  the last good ones until the next good compile is [#210](https://github.com/nanyx-lang/nanyx/issues/210).
- **No find-references or completion.** Each is one handler in `Handlers/`. Find-references can read the same
  `References` table, though only of the open document's own compile.
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
5. **Completion.** Filed: keep the last good compile, since completion is asked for while the text does not
   compile ([#210](https://github.com/nanyx-lang/nanyx/issues/210)); record where each symbol is in scope
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
