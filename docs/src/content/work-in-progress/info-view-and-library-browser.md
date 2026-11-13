---
title: "Info view and library browser"
description: "A panel that shows what the compiler knows at the cursor, and a browser and type search for the standard library"
order: 30
---

> **Status: proposal.** Doc comments and a first cut of the info view are done; nothing else here is built yet. This page sets out what the features are, what the compiler
> already records that they can use, what it would have to start recording, and an order to build them in. Each
> plan item has an issue under the `lsp` label. See [Language server and editor](./language-server) for what the server does
> today.

# Summary

Three editor features, each built on what the language server already knows:

1. **An info view.** A view in the side bar beside the editor that follows the cursor, modelled on the infoview in Lean 4's VS
   Code extension. It shows the type of the expression at the cursor and the names in scope with their types. It
   also shows the contexts in scope and where each comes from. In a long function, that last part shows which
   `use` or which part of the signature each call's contexts come from.
2. **Doc comments.** A `--|` comment above a definition is its documentation. Hover and completion show it, and
   the browser will.
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
| Where a call finds a parameterised context | `ContextInstances`: in scope (with the `use`, signature or inference that put it there), built from attached definitions, derived from a type's parts, or built in |
| What provides each context a call requires | `ContextProviders`: the signature, a `use`, inference, or the runtime |
| What each `use` loads | `UseContexts` (parameterised contexts only); the type of the value it loads, in `ExprTypes` |
| The contexts the runtime supplies at the top level | `AmbientContexts` |
| The library's modules and their doc comments | `StandardLibrary.moduleNames`, `Documentation.ofModule` |
| Opening a library definition | `StandardLibrarySource` writes a read-only copy of a module to open |
| Every export in the library | the export index `ImportFixes` builds once, to offer missing imports |
| Doc comments | attached by `DocComments.attach`, read for a symbol by `Documentation.Reader` |

Not recorded yet: **types of exports by symbol, for search.** These are in `ImportedTypes` only for what a module
imports, not for the whole library.

# 1. Info view

The first cut is built ([#715](https://github.com/nanyx-lang/nanyx/issues/715)), and "This call"
([#989](https://github.com/nanyx-lang/nanyx/issues/989)) and the context flow
([#717](https://github.com/nanyx-lang/nanyx/issues/717)) since: everything below but types as trees.
`NyxCompiler.InfoView.at` and `NyxCompiler.ContextFlow` gather it, `InfoViewHandler` answers the request, and
`extension/src/infoView.ts` is the panel (see [VS Code extension](../standard-tools/vs-code-extension#info-view)).

## What it shows

The panel follows the cursor. It shows, from top to bottom, in three groups with a rule between them (each section's
`Group`: `cursor`, `scope`, `contexts`):

- **At the cursor.** On a module's name, as `Hover.moduleAt` finds it, `module name` and the module's doc comment.
  Otherwise the type of the narrowest typed expression at the cursor, as hover shows it. On a name that refers
  to a generic def, the def's declared signature comes first, as hover writes the def (`def name: [contexts] type`),
  then the type at this use, then the bindings of the signature's type variables at this use (`a = string`, and a
  context variable as the contexts it stands for, `c = Console`), each linking to the def; `Hover.instantiationAt`
  works them out ([#929](https://github.com/nanyx-lang/nanyx/issues/929)). A def that is not generic shows its type once.
- **Messages.** The diagnostics at the cursor's line.
- **In scope.** The locals, parameters and pattern variables in scope, each with its type, nearest first. Top-level
  and imported names are collapsed by default.
- **Contexts.** The contexts in scope at the cursor. For each one, where it comes from:
  - the function's signature (`[Console]` in `def greet: [Console] ...`)
  - a `use` (a link to its line)
  - an inferred requirement (`[Show(α)]`, from `InferredContextTypes`)
  - the runtime, at the top level of a module

  A `use` comes first, nearest first, then the signature or inferred contexts of each def around the cursor,
  innermost first. Each context is written as hover writes it, with its type variables named together with the
  types of the locals.
- **This call.** On the innermost call or pipe around the cursor that requires contexts, each context it requires and
  where it finds that context: the provider from the list above, linked, or an instance the compiler builds, from
  attached definitions (named, `attached Point.show`), derived from the type's parts, or its own (`built in`)
  ([#989](https://github.com/nanyx-lang/nanyx/issues/989)). `InfoView.callContextsOf` reads the contexts the callee's
  type declares, at the types of the call, beside the call's `ContextInstances` in order, then the others
  `ContextProviders` records for it (a member's context, or one a function value it runs requires).
- **Context flow.** See below.

Every span in the panel is a link: clicking a `use` or a signature jumps to it.

## Following contexts through a long function

The info view answers "where does this call's `Console` come from?" for one call. For a whole function, its
*Context flow* section ([#717](https://github.com/nanyx-lang/nanyx/issues/717)) shows the innermost def around the
cursor: each context its signature writes, each it was inferred to require, then each `use` in it, and under each
one the calls it serves, linked. A `use` that serves no call is shown as unused. `ContextFlow.at` builds it from what
`InfoView.callContextsOf` finds for each call in the def, matching a provider by the span that links it (a
signature's context, the value a `use` loads, the def's name for an inferred one, with the context's name).

The same data decorates the editor: the response carries `highlights`, the ranges of the document to highlight, which
`ContextFlow.at` finds from the same search of the def as the flow. On a `use`, or a context of the def's signature, they are that provider and the calls it
serves; on a call, the call and the providers of its contexts. The extension draws them with the editor's word
highlight colours while the view is visible and not pinned, and, as `nanyx.infoView.contextLines` says, joins them
with lines down the left margin and across to each one (`ContextLines` in `extension/src/contextLines.ts`, drawn as
absolutely placed `before`/`after` pieces of empty decorations). They come with the info view's request, not
`textDocument/documentHighlight`: a server that answers that request replaces VS Code's own highlighting of the word
under the cursor everywhere else.

This needs the compiler to record, for each call, the provider of every context the callee requires, and it does
([#716](https://github.com/nanyx-lang/nanyx/issues/716)): `TypedModule.ContextProviders` holds, by the node of each
call or pipe, each context the call requires, as a type (`Console` is `TyApply("Console", [])`, `Show(Point)` is
`TyApply("Show", [Point])`), with what provides it there:

```fsharp
type ContextProvider =
    | FromSignature of SourceSpan        // the context as the def's annotation writes it
    | FromUse of NodeId                  // the value the `use` loads, as `UseContexts` is keyed
    | FromInference                      // an unannotated def, which now requires it itself
    | FromRuntime                        // the top level of a module
```

A call records the contexts its callee's type declares or was inferred to require, those a function value it runs
requires (through a context variable, see `ContextVariables`), and, for a call of a context's member (`println`),
that member's context. A context written out as a record has no name to record it by, and is left out.

It records what inference decides; it does not decide anything new. A member that a `use` or a def's signature
binds carries which of them binds it, in its `TypeScheme`'s `ProvidedBy` (`extendEnvWithRecordFields`,
`extendEnvWithContexts`), so the provider is that of the members the check in `Contexts.ensureContextAvailable`
finds. Where they are the ambient instance's, the provider is the runtime at the top level and inference in an
unannotated def (`Contexts.ambientProvider`). A call of an unannotated top-level def keeps what provides each ambient
context where it is (`DeferredContextUse.Providers`), and records the def's contexts once every def is inferred
(`Contexts.recordDeferredProviders`).

A parameterised context keeps its provider in `ContextInstances` itself, rather than in a table beside it:
`InstanceInScope of TypeExpr * ContextProvider`. Each instance put in scope (`bindInstanceOf` in
`ContextInstances.fs`) keeps the `use` or the signature that put it there beside it, in `InferState.InstanceContexts`,
and `instanceInScope` returns it with the instance. An instance a def that does not write its type was inferred to
require is provided by inference (`ContextInference.resolvePending`). Once the module is inferred,
`ContextInstances.recordInstanceProviders` adds an entry to `ContextProviders` for each `InstanceInScope` a call
found, before the contexts found by their members' names, as written where it is put in scope (`Show(a)`, over
variables of its own that keep its names). So there is one source of truth for an instance and what provides it, and
the backend, which reads `ContextInstances`, ignores the provider. An instance built from attached definitions
(`InstanceSynthesized`), derived from a type's parts (`InstanceDerived`) or the compiler's own (`InstanceBuiltIn`) has
no provider, and has no entry in `ContextProviders`; "This call" shows those from `ContextInstances`.

## Protocol and client

- **Request.** A custom request, `nanyx/infoView`, with a document and a position. It returns structured JSON:
  sections (`expression`, `inScope`, `topLevel`, `contexts`, `call`, `flow`, `messages`, always all of them, in that
  order), each with a title, whether it starts collapsed, and items, and the ranges to highlight (`highlights`). An
  item has its text, a detail (`parameter`, `use`, `error NYX0101`), a location to link to, the items under it (the
  calls a provider in the flow serves), and the doc comment of what it names, as Markdown with its links written
  out, as hover shows it ([#917](https://github.com/nanyx-lang/nanyx/issues/917)): for an import, its module's.
  `InfoView.at` gives each item's symbol and `InfoViewHandler` reads its doc with `Documentation.Reader.ForSymbol`. A
  context's doc is that of the def a `use` names, if it has one, or else the context's own. The client renders it with
  VS Code's Markdown renderer (`markdown.api.render`). It returns data, not rendered text, so the client can lay it out and
  link the spans. The server reads the same cached compile as hover (`DocumentStore`), and the last good compile
  while the text does not parse; the expression is left out where the text has been edited since.
- **Client.** A webview view in `extension/`, in a view container of its own in VS Code's secondary side bar
  ([#921](https://github.com/nanyx-lang/nanyx/issues/921); `contributes.viewsContainers.secondarySidebar` needs
  VS Code 1.106), revealed by *Nanyx: Show info view* (Ctrl+Shift+Enter, as in Lean). It sends the request when the
  selection or the document changes, debounced, while the view is visible, and drops an answer a later request has
  overtaken. It can pin a position, so the view keeps showing that position while you edit elsewhere; the pinned
  position does not move with edits above it yet. It shows each doc comment under its item, cut to its first
  paragraph until clicked, except that of what is at the cursor, which it shows in full. Plain TypeScript and
  HTML; no framework.
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
server shows a symbol's doc comment on hover and in completion items, read with `Documentation.Reader.ForSymbol`, which
parses the file the symbol is declared in (the document, a file beside it, or a standard-library module) for its doc
comments and keeps them by file. It turns each `` [`name`] `` into a link to the name's definition, looked up among
the names in scope where the comment is, then the top-level names of the comment's own file. A module's doc comment
is shown on hover over the module's name, in completion of import paths and with an import in the info view's
top-level list (`Documentation.Reader.ForModule`, [#937](https://github.com/nanyx-lang/nanyx/issues/937)).

Still open:

- Generating the standard library's docs pages from its doc comments
  ([#722](https://github.com/nanyx-lang/nanyx/issues/722)). Its modules, definitions, types, contexts and context
  members have doc comments ([#721](https://github.com/nanyx-lang/nanyx/issues/721),
  [#937](https://github.com/nanyx-lang/nanyx/issues/937)).
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

1. **Doc comments** ([#711](https://github.com/nanyx-lang/nanyx/issues/711)). Done, as
   [#719](https://github.com/nanyx-lang/nanyx/issues/719) designed them: hover and completion show them. Recording
   them by symbol is still open (see [Doc comments](#2-doc-comments)).
2. **Library requests** ([#712](https://github.com/nanyx-lang/nanyx/issues/712)). `nanyx/libraryModules` and
   `nanyx/libraryModule`, from the export index, with types and docs.
3. **Library browser** ([#713](https://github.com/nanyx-lang/nanyx/issues/713)). The tree view in the extension,
   opening definitions and inserting imports.
4. **Search by type** ([#714](https://github.com/nanyx-lang/nanyx/issues/714)). The matcher and ranking in the
   compiler (as a module beside `Completion`), `nanyx search`, then the search box and the quick pick.
5. **Info view, first cut** ([#715](https://github.com/nanyx-lang/nanyx/issues/715)). Done: `nanyx/infoView` with
   the expression, names in scope, contexts from the signature, a `use`, inference and the runtime, and messages; the
   webview panel with pinning.
6. **Context providers** ([#716](https://github.com/nanyx-lang/nanyx/issues/716)). Record a `ContextProvider` for
   every context at every call. Done (`ContextProviders`, and `InstanceInScope`'s provider), and shown as "This
   call" in the info view ([#989](https://github.com/nanyx-lang/nanyx/issues/989)).
7. **Context flow view** ([#717](https://github.com/nanyx-lang/nanyx/issues/717)). Done: the info view's *Context
   flow* section, and the editor highlights between a provider and its calls.

# Open questions

- Should the info view show types as trees, so each part can be hovered and followed to its definition, as Lean's
  does? That changes the request from strings to a typed tree, and the type printer would need to emit one.
- How loose should search be? Hoogle also matches a query in which an input is curried, or where a `list` could be
  a `maybe`. Each looser rule finds more, but buries the exact matches further down.
- Should the browser also be a page on the docs site, generated from the same data, so the library's reference is
  readable without an editor?
- Should user widgets come later? In Lean, a library can ship its own UI into the infoview. That is a long way off
  here, but `nanyx/infoView` returning sections of items leaves room for it.
