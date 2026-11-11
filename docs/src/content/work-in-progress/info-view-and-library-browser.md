---
title: "Info view and library browser"
description: "A panel that shows what the compiler knows at the cursor, and a browser and type search for the standard library"
order: 30
---

> **Status: proposal.** Nothing here is built yet. This page sets out what the features are, what the compiler
> already records that they can use, what it would have to start recording, and an order to build them in. Each
> plan item has an issue under the `lsp` label. See [Language server and editor](./language-server) for what the server does
> today.

# Summary

Three editor features, each built on what the language server already knows:

1. **An info view.** A panel beside the editor that follows the cursor, modelled on the infoview in Lean 4's VS
   Code extension. It shows the type of the expression at the cursor and the names in scope with their types. It
   also shows the contexts in scope and where each comes from. In a long function, that last part shows which
   `use` or which part of the signature each call's contexts come from.
2. **Doc comments.** The `--` comment directly above a definition is its documentation. Hover, completion and the
   browser show it.
3. **A library browser with search by type.** A view of the standard library's modules and what each exports,
   with each export's type and doc comment. You can search it by name or by type: type `bool -> string` and find
   what turns a `bool` into a `string`, without knowing its name.

# What the compiler already records

These features mostly read tables the server already has in `TypedModule` (see `TypedAst.fs`) for every compile:

| Need | Already recorded |
|------|------------------|
| Type at the cursor | `ExprTypes`, `CalleeTypes`, `SymbolTypes`, read by `Hover.typeAt` |
| Names in scope at the cursor | `Scopes`, read by `Completion.namesInScope` |
| Contexts an unannotated def needs | `InferredContextTypes` |
| Where a call finds a parameterised context | `ContextInstances`: in scope (from a `use` or the signature), built from attached definitions, or built in |
| What each `use` loads | `UseContexts` (parameterised contexts only) |
| The library's modules and their summaries | `StandardLibrary.moduleNames`, `StandardLibrary.trySummary` |
| Opening a library definition | `StandardLibrarySource` writes a read-only copy of a module to open |
| Every export in the library | the export index `ImportFixes` builds once, to offer missing imports |
| Comments and their spans | the parser's trivia (`LineComment`), which the formatter reads |

Some things are not recorded yet:

- **Unparameterised contexts.** `ContextInstances` has `None` for a context such as `Console`, whose instance is
  found by its members' names. Nothing records which `use` (or which part of the signature, or the runtime)
  provides a call's `Console`.
- **Doc comments.** The comments are kept as trivia, but none is attached to a definition.
- **Types of exports by symbol, for search.** These are in `ImportedTypes` only for what a module imports, not for
  the whole library.

# 1. Info view

## What it shows

The panel follows the cursor. It shows, from top to bottom:

- **Expression.** The type of the narrowest typed expression at the cursor, as hover shows it.
- **In scope.** The locals, parameters and pattern variables in scope, each with its type, nearest first. Top-level
  and imported names are collapsed by default.
- **Contexts.** The contexts in scope at the cursor. For each one, where it comes from:
  - the function's signature (`[Console]` in `def greet: [Console] ...`)
  - a `use` (a link to its line)
  - an inferred requirement (`[Show(α)]`, from `InferredContextTypes`)
  - the runtime, at the top level of a module
- **This call.** On a call to a function that requires contexts, each context it requires and where it finds that
  context: the provider from the list above, an instance built from attached definitions, or the compiler's own
  (`InstanceBuiltIn`).
- **Messages.** The diagnostics at the cursor's line.

Every span in the panel is a link: clicking a `use` or a signature jumps to it.

## Following contexts through a long function

The info view answers "where does this call's `Console` come from?" for one call. For a whole function, the
panel can switch to a flow view of the def that contains the cursor. It lists each `use` and each context in the
signature, and under each one, the calls it serves. A `use` that serves no call is shown as unused. The same data
can decorate the editor: when the cursor is on a `use`, highlight the calls it serves, and the reverse.

This needs the compiler to record, for each call, the provider of every context the callee requires, not just the
parameterised ones. A provider would be a new union, recorded by the call's node (in place of, or next to,
`ContextInstances`):

```fsharp
type ContextProvider =
    | FromSignature of SourceSpan        // the context as the def's annotation writes it
    | FromUse of NodeId                  // the `use` that loads it
    | FromInference                      // an unannotated def, which now requires it itself
    | FromRuntime                        // the top level of a module
    | Synthesized of (string * string) list
    | BuiltIn
```

Inference already resolves each requirement at the call, so this records what it decides; it does not decide
anything new.

## Protocol and client

- **Request.** A custom request, `nanyx/infoView`, with a document and a position. It returns structured JSON:
  sections of items, each with its text and spans. It returns data, not rendered text, so the client can lay it out
  and link the spans. The server reads the same cached compile as hover (`DocumentStore`), and the last good
  compile while the text does not parse.
- **Client.** A webview panel in `extension/`, opened by a command (*Nanyx: Show info view*) and a keybinding. It
  sends the request when the selection changes, debounced. It can pin a position, so the panel keeps showing that
  position while you edit elsewhere. Plain TypeScript and HTML are enough for this; nothing here needs a framework.
- **Other editors.** The request is plain LSP, so another client can show the same thing. A text rendering in an
  output buffer would do for Neovim.

Lean's infoview also lets you hover each part of a type shown in the panel. That needs the server to send types as
trees rather than strings. That is a later step; see [Open questions](#open-questions).

# 2. Doc comments

Settled in [#719](https://github.com/nanyx-lang/nanyx/issues/719) and done: a doc comment starts with `--|` and goes
on for the lines indented further than its first (see [Basics](../getting-started/basics#doc-comments)). The lexer
takes the continuation lines as part of the comment, and `DocComments.attach` (in `Nanyx.Parser/DocComments.fs`)
attaches each doc comment, by position, to the declaration whose name starts the next line with code: a def, a type
or context, a field or context member (by the span `DeclaredNames` has for it), a tag, or the module. The language
server shows a symbol's doc comment on hover and in completion items, read with `Documentation.forSymbol`, which
parses the file the symbol is declared in (the document, a file beside it, or a standard-library module) for its doc
comments and keeps them by file. It turns each `` [`name`] `` into a link to the name's definition, looked up among
the names in scope where the comment is, then the top-level names of the comment's own file.

Still open:

- Converting the standard library's `--` comments to doc comments
  ([#721](https://github.com/nanyx-lang/nanyx/issues/721)), and generating its docs pages from them
  ([#722](https://github.com/nanyx-lang/nanyx/issues/722)). `trySummary` still reads a module's first `--` line.
- Recording docs by symbol in `TypedModule`, so the info view and the browser read them as they read types, rather
  than parsing the declaring file again.
- A tag has no symbol, so its doc comment is attached but not shown anywhere yet; nor is a record field's on a
  member completion.
# 3. Library browser and search by type

## The browser

A tree view in VS Code's sidebar (*Nanyx library*):

- **Modules.** Each standard-library module, with its summary. The prelude comes first, since its names need no
  import.
- **Exports.** Under each module, its exports grouped by kind: functions, types and contexts, and attached
  definitions by the type they attach to (`string.trim` under `string`).
- **Each export** shows its type, its doc comment, and the import it needs (`import nanyx/text`). Opening it goes
  to its source, through `StandardLibrarySource`, as go-to-definition does. A button inserts the import into the
  active document, using the edit the missing-import quick fix already makes.

Go-to-definition on a standard-library type already works. The browser adds a way in for when there is no use of
the name to click on yet.

The server provides two requests: `nanyx/libraryModules` (each module's name and doc) and `nanyx/libraryModule`
(one module's exports, each with kind, name, type, doc and location). The library is checked once per server
process, so these are cheap. The packages a workspace depends on could be listed the same way later.

## Search by type

The search box takes a name, a type, or both. A type is parsed with the parser's own type grammar, so the query
is written as a signature is: `bool -> string`, `list(a), (a -> bool) -> list(a)`, `string -> list(string)`.

**What matches.** An export matches if its type can be made equal to the query's:

- **Instantiation.** The export's type variables can be instantiated. `a -> string` matches `bool -> string`, with
  `a = bool`.
- **Variables in the query.** These stand for any type, but consistently: `a -> a` does not match
  `int -> string`.
- **Order of inputs.** The inputs of a function that takes several can come in any order, since the query writer
  rarely knows which comes first. `(a -> bool), list(a) -> list(a)` finds `list.filter`.
- **Contexts.** An export's contexts must have an instance at the types it is instantiated at, or the match is
  shown with the contexts it still needs. `bool -> string` should find `ToString(a).toString`, which
  `ToString(bool)`, the compiler's own instance, satisfies.
- **Attached definitions.** These are found by the type they attach to, as the first input.

**Ranking.** Best first:

1. a type equal to the query, up to the names of its variables
2. a more general type instantiated
3. inputs reordered
4. contexts needed

Within each rank, shorter names first. Names that start with `__` (intrinsics and desugaring targets) are never
shown.

**Where it runs.** The index is built once, from the same export list as the browser. Each query is a unification
against every export, which is fast for a library this size. The query's variables are treated as fresh variables,
and an export's as instantiated ones. For a library much bigger than the standard library, the index can be keyed
by the type's shape (its result's head, its number of inputs) first.

**Where to find it.**

- the browser's search box
- a quick-pick command, *Nanyx: Search library by type*, which inserts the chosen function and its import
- `nanyx search "bool -> string"` on the command line, for the terminal and for agents, with the same results as
  text

Completion could later use the same ranking: offer the functions whose type fits the hole the cursor is in.

# Plan

In order. Each item is one PR:

1. **Doc comments** ([#711](https://github.com/nanyx-lang/nanyx/issues/711)). Attach the comment above each
   declaration, record docs by symbol, and show them in hover and completion. This is useful on its own, and the
   browser needs it.
2. **Library requests** ([#712](https://github.com/nanyx-lang/nanyx/issues/712)). `nanyx/libraryModules` and
   `nanyx/libraryModule`, from the export index, with types and docs.
3. **Library browser** ([#713](https://github.com/nanyx-lang/nanyx/issues/713)). The tree view in the extension,
   opening definitions and inserting imports.
4. **Search by type** ([#714](https://github.com/nanyx-lang/nanyx/issues/714)). The matcher and ranking in the
   compiler (as a module beside `Completion`), `nanyx search`, then the search box and the quick pick.
5. **Info view, first cut** ([#715](https://github.com/nanyx-lang/nanyx/issues/715)). `nanyx/infoView` with the
   expression, names in scope, contexts from the signature and `use`, and messages; the webview panel with pinning.
6. **Context providers** ([#716](https://github.com/nanyx-lang/nanyx/issues/716)). Record a `ContextProvider` for
   every context at every call, unparameterised ones included, and show "this call" in the info view.
7. **Context flow view** ([#717](https://github.com/nanyx-lang/nanyx/issues/717)). The flow view of a function, and
   the editor highlights between a `use` and its calls.

# Open questions

- Should the info view show types as trees, so each part can be hovered and followed to its definition, as Lean's
  does? That changes the request from strings to a typed tree, and the type printer would need to emit one.
- Should `ContextProvider` replace `ContextInstances`? The backend reads `ContextInstances`, so replacing it is a
  wider change than adding a table beside it.
- How loose should search be? Hoogle also matches a query in which an input is curried, or where a `list` could be
  a `maybe`. Each looser rule finds more, but buries the exact matches further down.
- Should the browser also be a page on the docs site, generated from the same data, so the library's reference is
  readable without an editor?
- Should user widgets come later? In Lean, a library can ship its own UI into the infoview. That is a long way off
  here, but `nanyx/infoView` returning sections of items leaves room for it.
