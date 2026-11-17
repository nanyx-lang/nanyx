---
title: "Info view and library browser"
description: "What is still open about the info view, doc comments, the library browser and search by type"
order: 30
---

> Doc comments, the info view, the library browser and search by type are built, and described for users on
> [VS Code extension](../standard-tools/vs-code-extension#info-view),
> [Basics](../getting-started/basics#doc-comments) and [The nanyx command](../standard-tools/command-line#the-standard-library).
> This page keeps what is [still open](#still-open) and the [open questions](#open-questions). Work is tracked under
> the `lsp` label.

The info view (`NyxCompiler.InfoView`, `NyxCompiler.ContextFlow`, the server's `nanyx/infoView` request and
`extension/src/infoView.ts`) is modelled on the infoview in Lean 4's VS Code extension. The library browser and search
by type (`NyxCompiler.LibraryCatalog`, `NyxCompiler.LibrarySearch`, the `nanyx/libraryModules`, `nanyx/libraryModule`,
`nanyx/librarySearch` and `nanyx/addImport` requests) read the exports of the checked standard library.

# Still open

- **Doc comments by symbol.** `Documentation.Reader` parses the file a symbol is declared in for its doc comments.
  Recording them by symbol in `TypedModule` would let the info view and the browser read them as they read types.
- **A tag's and a record field's doc comment.** A tag has no symbol, so its doc comment is attached but not shown
  anywhere yet; nor is a record field's on a member completion.
- **A `memory` block** is part of the scope it is in. Now that a block can name its `Memory`,
  `memory { context m -> … }` ([#1446](https://github.com/nanyx-lang/nanyx/issues/1446)), it may become a scope of
  its own in the info view, since it is where a scoped context lives.
- **A pinned position** does not move with edits above it.
- **Derived instances in search.** Search by type does not look for a context instance derived from a type's parts,
  such as a list's `ToString`, so such a match is shown as still needing it.
- **Other packages.** The browser and search list only the standard library. The packages a workspace depends on
  could be listed the same way.
- **Completion by type.** Completion could use the same ranking as search: offer the functions whose type fits the
  hole the cursor is in.
- **Bigger libraries.** Each query is a unification against every export, which is fast for a library the size of
  the standard library. For a much bigger one, the index can be keyed by the type's shape (its result's head, its
  number of inputs) first.

# Open questions

- Should the info view show types as trees, so each part can be hovered and followed to its definition, as Lean's
  does? That changes the request from strings to a typed tree, and the type printer would need to emit one.
- How loose should search be? Hoogle also matches a query in which an input is curried, or where a `list` could be
  a `maybe`. Each looser rule finds more, but buries the exact matches further down.
- Should the browser also be a page on the docs site, generated from the same data, so the library's reference is
  readable without an editor?
- Should user widgets come later? In Lean, a library can ship its own UI into the infoview. That is a long way off
  here, but `nanyx/infoView` returning sections of items leaves room for it.
- Should other editors get the info view? The request is plain LSP, so another client can show the same thing; a
  text rendering in an output buffer would do for Neovim.
