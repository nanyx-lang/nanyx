---
title: "Context-qualified types"
description: "Type-level context requirements for values and functions"
order: 1
---

This page defines the proposed syntax and semantics for attaching context requirements to any type, not just function definitions.

# Motivation

Today, context requirements are most visible on function signatures, for example:

```nanyx
def f: [C] (x, y) -> z
```

To support values like `MArray` and `MRef` safely, context requirements need to be expressible for value types as well.

# Syntax

Use the same syntax form already used by function signatures:

```nanyx
type MyType = [C] (int, string)
```

This means:
- The underlying shape is `(int, string)`.
- Values of `MyType` require context `C` to be used.

This keeps signatures and aliases aligned: any type expression from a signature can be extracted into a `type` alias unchanged.

# Core semantics

`[C] T` means a value of type `T` with required context `C`.

Requirements are part of the type and flow with the value:
- Assigning, returning, or storing values preserves requirements.
- Combining values unions requirements.
- A value can only be used where all its required contexts are available.

# Scope and escape

A scoped context can discharge its own requirement inside the scope, but not outside it.

```nanyx
def bad = {
  memory {
    def arr = MArray.of([1, 2, 3])
    arr
  }
}
```

The example above is rejected because the result still requires `$Memory` and escapes the `memory` scope.

In the standard library naming, `$Memory` is the context and `memory { ... }` is the helper that creates that context for a scope.

Converting to a context-free value before returning is valid:

```nanyx
def good = {
  memory {
    def arr = MArray.of([1, 2, 3])
    MArray.toList(arr)
  }
}
```

# Generality

This mechanism is intentionally generic and applies to any context, not only `$Memory`:
- mutable memory capabilities,
- IO capabilities,
- transaction or resource capabilities,
- user-defined contexts.

# Function types and lambdas

A function type carries the contexts its function requires, as a def's signature does: `[Console] int -> int`. Issue [#768](https://github.com/nanyx-lang/nanyx/issues/768) tracks the work. These points are settled:

- **A lambda requires the contexts it uses where it is called.** It doesn't capture them where it is made. A lambda that calls `bar`, a member of `Foo`, has a type like `[Foo] () -> string`, wherever it is made, even inside a def that declares `[Foo]` or a `use` that loads it. Using any member of a context requires the whole context, as it does for a def.
- **So a context cannot escape the scope that supplies it.** A `use` block that returns such a lambda returns a value that still requires `Foo`, and calling it outside needs `Foo` again:

  ```nanyx
  context Foo = (bar: string -> string)

  def fooEscapes: [Foo] string -> ([Foo] () -> string) = { s ->
    { bar(s) }
  }

  def escaped = use (bar = { s -> s }) in fooEscapes("hi")

  escaped()   // error: requires 'Foo'
  ```

- **A function that requires fewer contexts fits where one requiring more is expected.** A pure lambda can be passed to a parameter of type `[Console] a -> b`.
- **Higher-order functions take a context variable.** A single lowercase letter in a context list stands for any contexts: `list.map: [c] (list(a), [c] a -> b) -> list(b)`. A call with an argument that requires `Console` requires `Console` itself. An unannotated higher-order function infers its context variables, and the prelude's higher-order functions get `[c]`. See [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts).
- **A parameterised context at a concrete type is settled where the lambda is made.** `{ p -> describe(p) }` with `p: Point` finds its `Show(Point)` there, as a call does, and its type doesn't carry it. Only a context at a type variable, or a plain context such as `Foo`, stays in the lambda's type.

These rules land together. A lambda's type carrying `[Console]` without context variables would stop it being passed to `list.map` at all.

## What is implemented

A function type written in an annotation carries its contexts ([#797](https://github.com/nanyx-lang/nanyx/issues/797)). A parameter's, a result's, a record field's or a type alias's `[Console] string -> ()` keeps `Console`, and `nanyx check`, hover, type lenses and inlay hints show it: `[c] Option(a), ([c] a -> b) -> Option(b)`. `TyFunc` holds the contexts, and `FunctionContexts.fs` says when two lists of them agree. Two function types that must be the same require the same contexts. A function that requires fewer fits where one requiring more is expected, so a pure function can be passed for a `[Console] int -> int` parameter, but a `[Console] int -> int` can't be passed for an `int -> int` one.

Not yet done:

- A context variable such as `c` is kept and shown, but not checked, and a call doesn't bind it ([#798](https://github.com/nanyx-lang/nanyx/issues/798)).
- A lambda's type, and that of a named function used as a value, doesn't carry the contexts it uses ([#799](https://github.com/nanyx-lang/nanyx/issues/799)), and calling a function value doesn't require its contexts.

Until they are:

- A named function passed as a value needs its contexts where it is named, as a call to it does ([#773](https://github.com/nanyx-lang/nanyx/pull/773)).
- A lambda that uses a context's members is checked where it is written, against the def around it.
- Such a lambda can't leave the scope that supplies the context, as `memory` blocks reject a lambda over one of their cells ([#774](https://github.com/nanyx-lang/nanyx/issues/774)). A def whose type requires a context can't return, or return something holding, a lambda that uses it, and a `use ... in`, or a block with `use`, can't result in one. A lambda uses a context when it names one of its members, a function that requires it, or a local function that does either. The error is `NYX0225`, at the lambda, with the def or `use` as a related span. The runtime's ambient contexts, which the top level always has, are left out. `ContextEscapes.fs` holds the check, and `EscapingLambdas.fs` the walk it shares with `MemoryCells.checkEscape`. It is a stopgap: once function types carry contexts, the lambda's type says it requires `Foo`, and calling it where `Foo` isn't available is the error instead.
- In the Wasm backend, a lifted lambda captures the members its scope supplies: those of the def around it, by their parameters, and those each `use` in scope loads that it may call, by the locals lambda lifting evaluates the `use`'s bindings into. It may call one when it names it, names a function that requires contexts, or holds a closure that captured it. The lifted function loads them again with a `use` of its own ([#778](https://github.com/nanyx-lang/nanyx/issues/778)).

# Design notes

- Prefer wording like "requires context" over "can only exist".
- Context requirements should be tracked uniformly by type inference and assignability.
- Escape analysis for mutable values then falls out of standard type checking.
