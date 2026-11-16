---
title: "Context-qualified types"
description: "Which types carry contexts: function types, and types whose declaration says so"
order: 1
---

Decided by the maintainer in [#1059](https://github.com/nanyx-lang/nanyx/issues/1059): **a type carries contexts only
when it's a function type, or when its declaration says so.** An earlier draft of this page proposed `[C] T` for any
type, meaning "a value of `T` requires `C` to be used". That general form is dropped.

All of it is built, and described in [Contexts](../advanced/contexts#contexts-are-part-of-the-type). This page keeps
how it's built and the two gaps left, under "Still open" in each section.

# Function types

Function types carry their contexts: `[Console] int -> int`, context variables such as `[c]`, the contexts a lambda
uses, and a function that requires fewer contexts fitting where one requiring more is expected. The language docs
describe them in [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts) and
[Contexts](../advanced/contexts). How the backend passes such a function value is in
[Context calls](./context-calls#functions-that-require-contexts-as-values).

# A value holding a lambda

A record, tuple, list or tag holding a lambda doesn't take on the lambda's contexts, as
[Contexts](../advanced/contexts#contexts-are-part-of-the-type) describes. A context instance is never a value at all
([Contexts are not types](./context-escape#contexts-are-not-types)), so there's no `[Console] Log` either. A lambda
written in a top-level value, a named module's or a script's, is inferred outside it (`Contexts.outsideValue`), since
building the value does not run it ([#1522](https://github.com/nanyx-lang/nanyx/issues/1522)).

# Types whose declaration says so

A type declaration may write contexts before its body, in one of two forms. Only a type declared this way carries
contexts; writing contexts in front of any other type (`[Console] Point`, `x: [Console] int`) is an error.

## A scoped context: the type belongs to its scope

Done in [#1521](https://github.com/nanyx-lang/nanyx/issues/1521), and described in
[Types in a scope](../advanced/contexts#types-in-a-scope): `type Cursor = [Tx] protected (...)`, with NYX0252 for a
context that isn't one scoped context and NYX0253 for contexts on any other type that isn't a function.

How it works:

- The parser keeps the contexts as `TypeDefModifier.InScopeOf`, and `TypeDefInfo.ScopedBy` names the context.
- A nominal type's `TyNominal` holds the arguments it's applied to
  ([#1530](https://github.com/nanyx-lang/nanyx/issues/1530)). A type in a scope has one more, last: the scoped context
  itself, of the value's scope (`FunctionContexts.scoped`), which `tyToString` leaves out. A written `Cursor` takes the
  signature's scope of `Tx` (`TypeResolution.writtenScope`), as a written `mut T` takes `Memory`'s. A constructor call
  makes a scope of its own and records the context as used by the lambda around it (`freshNominalType`), so a block
  given to `transaction` that makes one ties it to the call's scope, and NYX0233 finds the scope in the call's result
  with no special case.
- A local def isn't generalised over a scope the lambdas around it use (`Infer.generalizeDef`), so `def c = open(...)`
  keeps the block's scope.
- A type whose written representation holds a value of a scope declares that scope's context
  ([#1604](https://github.com/nanyx-lang/nanyx/issues/1604)), as a function type never hides one:
  `type MStack(a) = [Memory] private MRef(list(a))`. Its body reads the context's scope as the type's own,
  `TypeDefInfo.Scope`, which `freshNominalType` and `nominalType` replace with the value's, so all one value holds
  belongs to one block. `TypeResolution.undeclaredHeldScopes` reports a context the body holds and the declaration
  doesn't write as NYX0256, with a fix that writes it before the `private` or `protected` (`TypeDefModifier.KindAt`).
  It counts the hidden scope arguments of the nominal types the body names, but not what a nominal type is made of,
  nor the contexts a function type requires. A type argument given at a use counts for nothing.
- The standard library declares `MRef`, `MArray`, `MStack` and `MList` this way (`type MRef(a) = [Memory] private box(a)`,
  [Mutation](./mutation#mutable-values)), with no special case for them.

Still open:

- A type in a scope is matched to its context by the context's name where it's declared, so a module that imports both
  under an alias, `t.Cursor` and `[t.Tx]`, gets a `Cursor` whose written scope is not the signature's `t.Tx`.

## A context variable: the type is generic in contexts

Done in [#1525](https://github.com/nanyx-lang/nanyx/issues/1525), and described in
[Types generic in contexts](../advanced/contexts#types-generic-in-contexts): `type Thunk(a) = [k] protected (get: [k]
() -> a)`, used as `[Console] Thunk(int)`, or `Thunk(int)` for the pure one. [Lazy sequences](./lazy-sequences) is
built on this.

How it works:

- The parser reads `[k]` as it reads `[Tx]` (`TypeDefModifier.InScopeOf`); a name that is a type variable
  (`Names.isTypeVariable`) is a context variable, kept as `TypeDefInfo.ContextVariable`, which the body's function types
  read as theirs. Two context variables, or one with a scoped context, are NYX0252 (`TypeResolution.typeScopeErrors`).
- A value carries its contexts as the last argument of its `TyNominal`, after a scope if it has one: a function type
  that requires them (`FunctionContexts.carried`), so that two of them agree, and one fits another, as function types
  do (`FunctionContexts.agree`, `fit`). `tyToString` writes them in front. `nominalType` puts them in for the body's
  variable. A type written without contexts carries none, and `[C] T` in a use is read by `typeExprToTy`; on a value
  def's annotation they are the type's, not the def's (`Contexts.extractDefContexts`).
- A constructor's value carries a variable of its own, which the functions it's made with widen, as a function type's
  variable keeps room for more; given where a value is expected, a variable nothing has settled carries the expected
  contexts (`Unifier`, the nominal case of assignability), and one left over when a def is generalised carries none
  (`Unifier.outputOnlyContextVars`). The lambdas a constructor is given are inferred outside a top-level value, as a
  record's are, and outside a function's body when the field's type requires contexts, so the value made carries what
  they use, not the function that makes it (`Contexts.asConstructorField`,
  [#1676](https://github.com/nanyx-lang/nanyx/issues/1676)).
- A context variable of a parameter's type binds at a call as a function type's does (`ContextVariables.pairs`), and
  one only the result writes, as in `([c] Thunk(a)), ([d] a -> b) -> [c, d] Thunk(b)`, isn't required by the call, in
  the backend too (`CoreIrLower.returnedVariables`).
- In the backend a value is a record of closures that take the members their contexts require. One given where a value
  that carries more is expected is a new record of its fields, each adapted (`CoreIrLowerMembers.coerce`): passed as
  an argument, as a def's result (read from its declared type), and named as a top-level value.
- A type generic in contexts can name itself, as `seq(a)`'s `#next(a, seq(a))` does, and is then generic in the same
  contexts there: the rest of a `[k] seq(a)` is a `[k] seq(a)` (`RecursiveNominals.close`,
  [#1663](https://github.com/nanyx-lang/nanyx/issues/1663)).

Still open:

- A generic def's result is still read from its value's type, so one whose value carries fewer contexts than it
  declares isn't adapted.

# No special cases

The standard library uses the same declarations a user would: nothing it does with contexts or scopes is out of a
user's reach. Only intrinsics stay private to it.

# Settled

- **Both forms on one declaration,** such as `[Memory, k]`, or two scoped contexts, are an error (NYX0252).
- **Separators.** Several contexts are separated by `,` in these declarations, as in every context list
  ([#1531](https://github.com/nanyx-lang/nanyx/issues/1531)).
