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
`[Console] Log` either.

Not yet: a top-level value that only holds such a lambda is reported as needing the context to be computed
([#1522](https://github.com/nanyx-lang/nanyx/issues/1522)).

# Types whose declaration says so

A type declaration may write contexts before its body, in one of two forms. Only a type declared this way carries
contexts; writing contexts in front of any other type (`[Console] Point`, `x: [Console] int`) is an error.

## A scoped context: the type belongs to its scope

([#1521](https://github.com/nanyx-lang/nanyx/issues/1521))

```nanyx fragment
scoped export context Tx = private (...)

export type Cursor = [Tx] protected (...)
```

A value of `Cursor` carries the scope of the `Tx` it was made under, as `mut T` carries its block's. It's written
`Cursor`; the scope is never shown. [NYX0233](./context-escape#scoped-contexts) keeps it, and anything holding it,
inside that `Tx`. Its operations require `Tx` as usual, and holding one requires nothing.

The standard library declares `MRef`, `MArray`, `MStack` and `MList` this way (`type MRef(a) = [Memory] protected (...)`,
[Mutation](./mutation#planned)), with no special case for them.

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

- **Both forms on one declaration,** such as `[Memory & k]`, or two scoped contexts: an error to start with, unless
  the implementation finds it falls out.
- **Separators.** Several contexts are joined with `,`, not `&`, in these declarations as in every context list
  ([#1531](https://github.com/nanyx-lang/nanyx/issues/1531)).
