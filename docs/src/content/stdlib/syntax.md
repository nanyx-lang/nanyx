---
title: syntax
description: The syntax tree a linter is given
order: 17
---

`syntax` is in `nanyx.analysis`, the package a linter is written against: the module it checks, as its source writes it.
A linter matches on the tree's nodes and asks [`Analysis`](./analysis) what type checking found out about them, by
their ids. How linters run is on [Linters: compiler design](../work-in-progress/linters).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

The syntax tree of a module as its source writes it, which a linter is given to check.

The tree is the module before the compiler desugars it, with its attributes, so that a rule
reports what the user wrote and a fix edits it. Each node is a record of its `id`, its `span` and
its `kind`: the `id` is what [`Analysis`](analysis) answers questions about, such as the type of
an expression or the symbol a name refers to, and the `kind` is a union a rule matches on.
Constructs the compiler desugars before checking, such as a `for` loop, appear as the calls they
are desugared to.

## `NodeId`

```nanyx fragment
type NodeId = int
```

Identifies a node of the tree, for the questions [`Analysis`](analysis) answers about it.

## `Span`

```nanyx fragment
type Span = (start: int, end: int, line: int, column: int)
```

Where a node is written: offsets into the module's text, its line endings made `\n` (`end` is
one past the last character), and the zero-based line and column of `start`.

## `Literal`

```nanyx fragment
type Literal = #string(string) | #int(int) | #float(float) | #bool(bool) | #measure(number: string, unit: string)
```

A literal value as written. A number with a unit straight after it, `500ms`, is a `#measure` of
the number as written and the unit's name.

## `ImportSource`

```nanyx fragment
type ImportSource = #module(string) | #path(string)
```

Where an import comes from: a module by its name, `nanyx/text`, or a file by its path.

## `Import`

```nanyx fragment
type Import = (source: ImportSource, alias: #some(string) | #none, selection: #some(list((string, Span))) | #none, span: Span)
```

One module an import brings in: where from, the alias it is given, the names `as (a, b)`
selects, each where it is written, and where the import is written.

## `TypeModifier`

```nanyx fragment
type TypeModifier = #export | #context | #private | #protected | #scoped(Span) | #inScopeOf(list((string, Span))) | #kindAt(Span) | #associatedTypes(list(string))
```

What a type definition's declaration says beside its body: that it is exported, a context,
private, protected or scoped, the scope of the scoped context its values belong to, where its
`private` or `protected` is written, and a context's associated types.

## `TopLevelItem`

```nanyx fragment
type TopLevelItem = (id: NodeId, span: Span, kind: member0)
```

A node of the module: an item at its top level, a statement of a block, an expression, a
pattern or a type written in it.

## `TopLevelItemKind`

```nanyx fragment
type TopLevelItemKind = #moduleDeclaration(isExport: bool, name: string) | #moduleBlock(name: string, items: list(member1)) | #def(isExport: bool, name: string, annotation: #some(TypeExpr) | #none, value: Expr) | #typeDef(TypeDefinition) | #import(list(Import)) | #reExport(Import) | #expr(Expr) | #attribute(name: string, argument: #some(Expr) | #none) | #dimension(isExport: bool, name: string, storage: #some(TypeExpr) | #none, derived: #some(TypeExpr) | #none) | #unitDeclaration(isExport: bool, name: string, dimension: TypeExpr, multiple: #some(Expr) | #none)
```

What a top-level item is.

## `TypeDefinition`

```nanyx fragment
type TypeDefinition = (name: string, modifiers: list(TypeModifier), parameters: list((string, #some(member12) | #none)), body: member12)
```

A type definition: its name, its modifiers, its parameters, each with its constraint if it
writes one, and its body.

## `Statement`

```nanyx fragment
type Statement = (id: NodeId, span: Span, kind: member1)
```

A statement of a block.

## `StatementKind`

```nanyx fragment
type StatementKind = #def(isExport: bool, name: string, annotation: #some(member12) | #none, value: member9) | #typeDef(member0) | #import(list(Import)) | #use(member9) | #expr(member9) | #attribute(name: string, argument: #some(member9) | #none)
```

What a statement is.

## `Expr`

```nanyx fragment
type Expr = (id: NodeId, span: Span, kind: member8)
```

An expression. The span of a `#call`, `#pipe` or `#member` is the whole expression's, and its
`nameSpan` the name's.

## `ExprKind`

```nanyx fragment
type ExprKind = #literal(Literal) | #unit | #name(string) | #call(name: string, nameSpan: #some(Span) | #none, arguments: list(member9)) | #lambda(parameters: list((string, #some(member12) | #none)), body: member9, takesUnit: bool) | #contextNames(names: list((string, #some(member12) | #none, Span)), body: member9) | #binary(operator: string, operatorSpan: #some(Span) | #none, left: member9, right: member9) | #pipe(value: member9, name: string, nameSpan: #some(Span) | #none, arguments: list(member9)) | #block(list(member2)) | #match(values: list(member9), arms: list(member5)) | #except(value: member9, arms: list(member5)) | #tuple(list(member9)) | #record(list(member6)) | #list(list(member9)) | #tag(name: string, payload: #some(member9) | #none) | #if(condition: member9, whenTrue: member9, whenFalse: member9) | #member(target: member9, field: string, fieldSpan: #some(Span) | #none) | #contextMember(context: member12, member: string) | #contextCall(context: member12, member: string, arguments: list(member9)) | #typeValue(member12) | #useIn(value: member9, body: member9) | #bind(keyword: string, value: member9) | #return(member9) | #interpolated(list(member7))
```

What an expression is.

## `MatchArm`

```nanyx fragment
type MatchArm = (patterns: list(member4), body: member9)
```

An arm of a `match`: its patterns, one for each value matched, and its body.

## `RecordField`

```nanyx fragment
type RecordField = #named(string, member9) | #positional(member9)
```

A field of a record literal: `name = value`, or a value by its position.

## `StringPart`

```nanyx fragment
type StringPart = #text(string) | #expr(member9)
```

A part of an interpolated string: text as written, or an expression in braces.

## `Pattern`

```nanyx fragment
type Pattern = (id: NodeId, span: Span, kind: member3)
```

A pattern.

## `PatternKind`

```nanyx fragment
type PatternKind = #literal(Literal) | #name(string) | #wildcard | #unit | #else | #tuple(items: list(member4), hasRest: bool) | #constructor(name: string, patterns: list(member4)) | #members(members: list((string, member4)), hasRest: bool) | #list(items: list(member4), rest: #some(member4) | #none) | #listMiddle(before: list(member4), after: list(member4)) | #range(from: member9, to: member9) | #guard(member9) | #tag(name: string, payload: #some(member4) | #none) | #argumentTag(name: string, arguments: member9, payload: #some(member4) | #none) | #and(list(member4))
```

What a pattern is. A list pattern's `rest` is the pattern after `...` at its end, and a
`#listMiddle` has its `...` between the patterns before it and those after.

## `TypeExpr`

```nanyx fragment
type TypeExpr = (id: NodeId, span: Span, kind: member11)
```

A type as written.

## `TypeExprKind`

```nanyx fragment
type TypeExprKind = #name(string) | #variable(string) | #hole | #unit | #tuple(list(member12)) | #record(list(member10)) | #openRecord(fields: list(member10), rest: #some(string) | #none) | #apply(name: string, arguments: list(member12)) | #function(input: member12, output: member12) | #tag(name: string, payload: #some(member12) | #none) | #union(list(member12)) | #intersection(list(member12)) | #contexts(list(member12)) | #withContexts(contexts: list(member12), inner: member12) | #constraint(name: string, inner: member12, predicate: member9) | #where(inner: member12, predicate: member9) | #typeOf(member9) | #typeKeyword | #namedArgument(name: string, value: member12) | #associated(context: member12, name: string) | #typeArgument(member12) | #product(member12, member12) | #quotient(member12, member12)
```

What a written type is.

## `TypeField`

```nanyx fragment
type TypeField = #field(name: string, isMutable: bool, annotation: #some(member12) | #none, default: #some(member9) | #none) | #attribute(name: string, argument: #some(member9) | #none, span: Span)
```

A field of a record type: a named field, with whether it is `mut`, its type and its default
value, or an attribute written before the field after it.

## `Module`

```nanyx fragment
type Module = list(TopLevelItem)
```

A module: its top-level items in the order written.

<!-- End of the generated reference. -->
