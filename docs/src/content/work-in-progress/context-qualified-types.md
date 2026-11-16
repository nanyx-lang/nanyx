---
title: "Context-qualified types"
description: "Which types carry contexts: function types, and types whose declaration says so"
order: 1
---

Decided by the maintainer in [#1059](https://github.com/nanyx-lang/nanyx/issues/1059): **a type carries contexts only
when it's a function type, or when its declaration says so.** An earlier draft of this page proposed `[C] T` for any
type, meaning "a value of `T` requires `C` to be used". That general form is dropped.

# Function types

Function types carry their contexts: `[Console] int -> int`, context variables such as `[c]`, the contexts a lambda
uses, and a function that requires fewer contexts fitting where one requiring more is expected. The language docs
describe them in [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts) and
[Contexts](../advanced/contexts). How the backend passes such a function value is in
[Context calls](./context-calls#functions-that-require-contexts-as-values).

# A value holding a lambda

A record, tuple, list or tag holding a lambda doesn't take on the lambda's contexts. Each function inside it keeps its
own, and calling it requires them where it's called:

```nanyx fragment
def handlers = (log = { m: string -> println(m) }, count = { n: int -> n + 1 })
-- (log: [Console] string -> (), count: int -> int)

def pureCaller: () -> () = {
  def f = handlers.log
  f("x")          -- NYX0211: 'f' requires the context 'Console'
}
```

Holding, passing or storing `handlers` requires nothing, and `handlers.count` can be called from pure code. A context
instance is never a value at all ([Contexts are not types](./context-escape#contexts-are-not-types)), so there's no
`[Console] Log` either. A top-level value that only holds such a lambda needs no context to be computed: a lambda written
in a value, a named module's or a script's, is inferred outside it (`Contexts.outsideValue`), since building the value
does not run it ([#1522](https://github.com/nanyx-lang/nanyx/issues/1522)).

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

Still open:

- The standard library declares `MRef`, `MArray`, `MStack` and `MList` this way (`type MRef(a) = [Memory] protected (...)`,
  [Mutation](./mutation#planned)), with no special case for them: [#1491](https://github.com/nanyx-lang/nanyx/issues/1491)
  and [#1493](https://github.com/nanyx-lang/nanyx/issues/1493).
- A type in a scope is matched to its context by the context's name where it's declared, so a module that imports both
  under an alias, `t.Cursor` and `[t.Tx]`, gets a `Cursor` whose written scope is not the signature's `t.Tx`.
- Each call that requires a scoped context requires it of the callee's own scope, so two such calls in one block make
  it require the context twice ([#1553](https://github.com/nanyx-lang/nanyx/issues/1553)), and a context with no members
  is satisfied where nothing supplies it ([#1479](https://github.com/nanyx-lang/nanyx/issues/1479)).

## A context variable: the type is generic in contexts

([#1525](https://github.com/nanyx-lang/nanyx/issues/1525))

```nanyx fragment
type seq(a) = [k] protected (next: [k] () -> #next(a, seq(a)) | #done)

def lines: [FileSystem] string -> [FileSystem] seq(string) = ...
```

`[k]` makes the type generic in contexts, as `[c]` makes a function type generic. A use writes the contexts in front,
`[FileSystem] seq(string)`, or none for the pure one. Calling the functions inside requires them, and a type with fewer
contexts fits where one with more is expected. [Lazy sequences](./lazy-sequences) is built on this.

# No special cases

The standard library uses the same declarations a user would: nothing it does with contexts or scopes is out of a
user's reach. Only intrinsics stay private to it.

# Open questions

- **Both forms on one declaration,** such as `[Memory, k]`, or two scoped contexts: an error to start with, unless
  the implementation finds it falls out.
- **Separators.** Several contexts are separated by `,` in these declarations, as in every context list
  ([#1531](https://github.com/nanyx-lang/nanyx/issues/1531)).
