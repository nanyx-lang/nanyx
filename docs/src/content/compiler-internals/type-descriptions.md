---
title: "Type descriptions"
description: "How a match on a Type value is checked and what it compiles to"
order: 16
---

A `match` on a `Type(a)` inspects the type it stands for, as [Inspecting a type](../tour/functions#inspecting-a-type)
describes. This page says how the compiler checks one and what it compiles to. What is still planned is on
[Type members and reflection](../work-in-progress/type-members-and-reflection) in Work in progress.

# Checking

`Infer.inferMatch` hands a match whose one value is a `Type(a)` to `TypeMatches.infer`. The tags a description has, and
the built-in description types (`RecordInfo(a)`, `UnionInfo(a)`, `NominalInfo`, `Field(a)`, `Case(a)`, `SomeType`)
with their members, are in `TypeValues`, which the backend reads too.

- **Narrowing.** Where `a` is a type variable, or a type the checker never learns, an arm whose tag says what it is is
  checked with `a` written as that (`TypeMatches.narrowEnv`) in the types of the names in scope (only names whose
  schemes have free variables, `TypeEnv.Unquantified`, can hold it) and in the type the match is expected to give. A
  lambda's declared result reaches a match that ends its body (`TypeMatches.expectResult`), and a def's block result
  reaches one that ends the block (`ExpectedBlockResults`). Each arm's body is checked against that narrowed result
  with `Visibility.addDeclaredConstraintAt`. Narrowing is not unification: the constraints stay global, and only the
  arm's view of the names in scope changes.
- **Packaged types.** A field, a case, a type argument and a collection's element each have a type the checker never
  learns, a skolem (`TypeDescriptions.skolem`): a `TyApply` with no arguments whose name, `b#12`, no other type has, so
  it unifies only with itself. Diagnostics write it `b`. Reading a member of a field opens it once per name
  (`InferState.OpenedPackages`, by the name's symbol), so `f.get(value)` and `f.type` agree. A value of one can't leave
  the lambda that opened it (`TypeDescriptions.escapingParameters`, checked in `Infer.inferLambdaAfterTypeOfs`) or the
  arm whose tag made it (`TypeDescriptions.escaping`), NYX0274.
- **Recursion.** A walker calls itself at a part's type, so a def whose annotation is complete sees itself, while its
  body is checked, generic in the variables it writes (`InferModule`, where it binds `envWithRec`).
- **For the backend,** each arm that narrows `a` records the type it narrows it to at its pattern's node, in
  `ExprTypes`.

# Compiling

A `Type` value is a number at run time, an `IrInt`: the number of the type it stands for in the module
(`CoreIrTypeIds`). `type X` is that number as a constant, and each type gets one when it is first met. The module's
`__describe` function, lowered after every other function (`CoreIrLowerModule`), builds the description of the type of
each number: a tag of `TypeValues.descriptionUnion`, with a record's `RecordInfo` and its fields, each field's type as
a number in turn, so `__describe` goes on until every type numbered has one. The module holds descriptions of the
types its `Type` values name and of the types of their parts, and of no others.

`FunctionLowering.Match` hands a match on a `Type` to `CoreIrLowerTypeMatch.lower`:

- **A known type.** In a function specialised at a type it knows, the match picks its arm at compile time. An arm that
  narrows `a` matches if what it narrows it to fits the type, and what that finds of its skolems, the element type of a
  `#list(elem)` arm, is bound from there on (`CoreIrLowerTypes.substitute` maps a skolem by its number). An arm that
  reads nothing of the description is lowered alone; one that does matches on what `__describe` gives.
- **A type only known when the program runs.** A skolem left unbound is `boxedTy`, so a function called at a field's
  type, as a walker is, is specialised once with that type's values boxed (`IrBox`, as a context member's own type
  variables are, see [Context calls](context-calls)). Its match matches on `__describe` of the number it is given. In
  an arm that narrows `a`, a boxed name is unboxed where it is used (`FunctionLowering.Identifier`), and the arm's value
  is boxed back to the match's type (`FunctionLowering.Branch`), both through `CoreIrLowerBoxes.convert`.
- **Canonical boxes.** For that to work whatever the value is, a boxed value of a collection holds its elements boxed:
  a list, a set or an array of boxes, and a map whose entries are tuples of boxes. A record or a union is boxed as it
  is, since only its description's `get`s read it. A field's `get` (`fieldGettingFunction`) and a case's `get`
  (`caseGettingFunction`) are closures over functions of their own that unbox the value, read the part, and box it in
  this form, through a function per collection type (`canonicalisingFunction`).
