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

[Contexts as attributes](./context-attributes) proposes writing the contexts as attributes on the type, `@C (int, string)`, in signatures and aliases alike.

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

The example above is rejected because the result still requires `Memory` and escapes the `memory` scope.

In the standard library naming, `Memory` is the context and `memory { ... }` is the helper that creates that context for a scope.

Requiring `Memory` isn't enough on its own, because every `memory` block supplies one. [Context escape](./context-escape#scoped-contexts) settles the rule that stops such a value leaving its block: `Memory` is a scoped context, and each `memory` call starts a scope that nothing requiring it can leave.

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

This mechanism is intentionally generic and applies to any context, not only `Memory`:
- mutable memory capabilities,
- IO capabilities,
- transaction or resource capabilities,
- user-defined contexts.

# Function types

Function types already carry their contexts: `[Console] int -> int`, context variables such as `[c]`, the contexts a lambda uses, and a function that requires fewer contexts fitting where one requiring more is expected. The language docs describe them in [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts) and [Contexts](../advanced/contexts). How the backend passes such a function value is in [Context calls](./context-calls#functions-that-require-contexts-as-values). What is still open is a context on a type that is not a function: `WrittenContexts.fs` drops the contexts written on any other type, so `type MyType = [C] (int, string)` is not built yet.

## How it differs from the design above

The design is that a lambda requires the contexts it uses where it is called, and carries any context it uses at a
type variable. The type checker departs from that in these ways, for now:

- **A lambda's body is checked against the contexts where it is written,** so `withConnection { insert(...) }` in a def that doesn't declare `Connection` is an error at `insert`.
- **A parameterised context is never carried,** even at a type variable: it is found where the lambda is made, as before.
- **A lambda written inside a `use` of a record written out doesn't require a context:** `use (bar = ...) in { bar("x") }` is a `() -> string`, since the record names no context, and the lambda keeps the member it captured where it was made. A `use` of a value whose type names the context, `use Foo(bar = ...) in { bar("x") }`, gives the lambda `[Foo] () -> string` ([#844](https://github.com/nanyx-lang/nanyx/issues/844)): the `use` binds each member with the symbol `Foo` declared it with, as a def that declares `[Foo]` does.
- **A call reads what a context variable stands for from the types solved so far,** and lambda parameters take their types from the constraints only while they all agree, so an earlier type error can leave a context unbound or a parameter's type unknown.

# Design notes

- Prefer wording like "requires context" over "can only exist".
- Context requirements should be tracked uniformly by type inference and assignability.
- Escape analysis for mutable values then falls out of standard type checking.
