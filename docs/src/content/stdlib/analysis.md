---
title: analysis
description: What a linter asks about a module, and what it reports
order: 17.1
---

`analysis` is in `nanyx.analysis`, the package a linter is written against. A linter's `lint` is given the module's
[syntax tree](./syntax) and asks the `Analysis` context about it, and gives back a list of `Report`s. How linters
run is on [Linters: compiler design](../work-in-progress/linters).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

What a linter knows about the module it checks, and what it reports.

A linter is a package whose root module exports `lint: [Analysis] Module -> list(Report)`. It is
given the module's [syntax tree](syntax), and asks [`Analysis`](#analysis) what type checking found out
about it: the type of an expression, the symbol a name refers to, the places that refer to a
symbol. Each [`Report`](#report) says which of the linter's rules found something, where, and how to fix
it; the compiler gives it the rule's code and the severity the project sets for it.

## `Ty`

```nanyx fragment
type Ty = #variable(string) | #primitive(string) | #named(name: string, arguments: list(self), isNominal: bool) | #function(input: self, output: self, contexts: list(self)) | #record(list((string, self))) | #openRecord(list((string, self))) | #tag(name: string, payload: #some(self) | #none) | #union(tags: list(self), isOpen: bool) | #applied(name: string, arguments: list(self)) | #other(string)
```

A type as type checking found it. A `#named` type is an alias or a nominal type, written by its
name and its arguments rather than what it stands for. A `#function` requires its `contexts`
where it is called. `undefined`, the type of what never gives a value, is the `#union` of no
tags. A type the compiler can't describe here is `#other`, as it writes it.

## `SymbolId`

```nanyx fragment
type SymbolId = int
```

Identifies a symbol, for the questions [`Analysis`](#analysis) answers about it.

## `SymbolKind`

```nanyx fragment
type SymbolKind = #topLevel | #local | #parameter | #patternVariable | #importedModule | #typeDefinition | #contextMember | #unitOfMeasure
```

What a symbol is: a def at the top level of a module or in a block, a lambda's parameter, a
name a pattern binds, an import, a type or a context, a member of a context, or a unit of
measure.

## `Symbol`

```nanyx fragment
type Symbol = (id: SymbolId, name: string, kind: SymbolKind, declaration: #some(Span) | #none, isExported: bool)
```

Something a name can refer to: its name, what it is, where in this module it is declared (`#none`
for one declared in another module, or that the compiler made up), and whether its module exports
it.

## `DocComment`

```nanyx fragment
type DocComment = (span: Span, text: string, documents: #declaration(Span) | #tag(Span) | #module | #nothing)
```

A doc comment: where it is written, its text as written, and what it documents: the declaration
whose name is written at the span, the tag written there, the module, or nothing it can document.

## `TextEdit`

```nanyx fragment
type TextEdit = (span: Span, newText: string)
```

An edit to the module's text: what `span` covers replaced by `newText`.

## `Fix`

```nanyx fragment
type Fix = (title: string, edits: list(TextEdit))
```

A fix for what a report found: its title, as the editor offers it, and its edits.

## `Report`

```nanyx fragment
type Report = (rule: string, span: Span, message: string, subject: #some(string) | #none, related: list((Span, string)), fixes: list(Fix))
```

Something one of a linter's rules found: the rule's `id`, where, the message, the one name the
headline shows after the rule's title (the binding an unused-binding report is about), other
places it relates to, each with what it is, and its fixes.

## `Analysis`

```nanyx fragment
context Analysis = (
  source: () -> string
  typeOf: NodeId -> #some(Ty) | #unknown
  calleeTypeOf: NodeId -> #some(Ty) | #unknown
  symbolOf: NodeId -> #some(Symbol) | #none
  importOf: NodeId -> #some(Symbol) | #none
  symbol: SymbolId -> #some(Symbol) | #none
  symbols: () -> list(Symbol)
  typeOfSymbol: SymbolId -> #some(Ty) | #unknown
  contextsOf: SymbolId -> list(Ty)
  referencesTo: SymbolId -> list(NodeId)
  lowered: NodeId -> list(NodeId)
  describe: Ty -> string
  coveringArms: NodeId, int -> list(int)
  redundantTypeName: NodeId -> bool
  docComments: () -> list(DocComment)
)
```

What type checking found out about the module a linter checks, asked by node or by symbol.

- `source`: The module's text, its line endings made `\n`: what spans are offsets into.
- `typeOf`: The type of an expression, or `#unknown` where the compiler doesn't know it.
- `calleeTypeOf`: The type of the function a call or pipe calls there, with the contexts it requires.
- `symbolOf`: The symbol a name, call, pipe target or qualified name refers to, as written.
- `importOf`: The import a name came in through.
- `symbol`: A symbol by its id.
- `symbols`: The symbols the module declares: its definitions, types, parameters, pattern variables and
  imports, each with where it is declared.
- `typeOfSymbol`: The type of a declaration.
- `contextsOf`: The contexts a top-level def requires, written or inferred.
- `referencesTo`: The nodes in the module that refer to a symbol.
- `lowered`: The nodes desugaring made for a node as written, such as a workflow bind's call, whose types
  `typeOf` has too.
- `describe`: A type written as the compiler's messages and hover write it.
- `coveringArms`: Of the `match` at the node, the positions of the arms before the arm at the position given
  that between them match every value it does, from the pattern space exhaustiveness checking
  uses: none for an arm that can match.
- `redundantTypeName`: Whether a pipe's target is written with a type's name that the name without it reaches as
  well, as in `xs \list.map` where `xs` is a list.
- `docComments`: The module's doc comments, each with what it documents.

<!-- End of the generated reference. -->
