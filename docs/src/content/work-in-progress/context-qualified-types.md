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

Requiring `$Memory` isn't enough on its own, because every `memory` block supplies one. [Context escape](./context-escape#scoped-contexts) settles the rule that stops such a value leaving its block: `$Memory` is a scoped context, and each `memory` call starts a scope that nothing requiring it can leave.

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

A single lowercase letter in a context list is a context variable ([#798](https://github.com/nanyx-lang/nanyx/issues/798)). A def's own context list binds it: `def g: [d] ([c] int -> int) -> int` is `NYX0226` at `c`. A call binds each of the callee's context variables to the contexts of the argument given for it, and requires them as if the callee wrote them. So passing a `[Console] int -> int` to `apply: [c] ([c] int -> int), int -> int` needs `Console` where the call is, or infers it in an unannotated def. A variable bound to a pure function requires nothing. Hover at the call shows the variable filled in. `ContextVariables.fs` holds this. The call reads what the variables stand for from the types solved so far, so an argument whose type isn't known there yet binds nothing.

A lambda's type carries the contexts it uses ([#799](https://github.com/nanyx-lang/nanyx/issues/799)): `{ n -> println(n) }` is `[Console] int -> ()`, and calling it requires `Console`. `LambdaContexts.fs` records, for the innermost lambda being inferred, each context that something in its body requires where it runs: a call to a function that requires it or to one of its members, a call to a function value whose type requires it, what a call's context variables stand for, and a read or change of a `memory` block's cell, which requires `$Memory` (`MemoryCells.recordUse`, [#1055](https://github.com/nanyx-lang/nanyx/issues/1055)). A lambda made inside another is not run by it, but a `for` loop's body and a builder block are. A def's own body is not such a lambda: its contexts are the def's. A local def that doesn't write its type and calls itself, such as `rec loop = { ... }`, requires what its lambda uses: a call to itself from that lambda uses nothing more, and the context variable the call's type has stands for exactly the def's contexts (`LambdaContexts.enterSelf`), so `loop` over a list is `list(a) -> int`, not `[c] list(a) -> int` ([#887](https://github.com/nanyx-lang/nanyx/issues/887)). A context variable that no caller can choose, one in no function's input, stands for no contexts: a def is generalised with it solved to none, as a union rest nothing can add to is (`Unifier.outputOnlyContextVars`). So a local `rec` def that calls itself through a function value, as `def next = loop(k - 1)` then `next(total + 1)` does, is `int -> int -> int`, not `int -> ([c] int -> int)` ([#904](https://github.com/nanyx-lang/nanyx/issues/904)). A named function used as a value has its contexts in its type (`ContextVariables.ofNamedValue`). A function that requires contexts given where a function that requires fewer is expected is `NYX0227`, so `runPure(mk())` from [#768](https://github.com/nanyx-lang/nanyx/issues/768) is an error at `mk()`.

An unannotated top-level def infers its context variables ([#800](https://github.com/nanyx-lang/nanyx/issues/800)): calling a function parameter, or passing one to a higher-order function, gives it a context variable, which the def names as an annotation would (`ContextInference.nameContextVariables`). `def apply = { f, x -> f(x) }` is `[c] ([c] a -> b), a -> b`. An imported def keeps the names of its context variables, which a call binds them by.

A context's member is a function type like any other, so one that uses another context says so: `context Log = (log: [Console] string -> ())`. Calling `log` then requires `Console` as well as `Log`. A constructor's lambda fields take their parameters' types from the members (`LambdaArguments.namedFields`). A `use` may give `log: string -> ()` a lambda that prints, and `Console` is required where the `use` is written ([Context escape](./context-escape#discharging-one-context-into-another), [#1054](https://github.com/nanyx-lang/nanyx/issues/1054)): `UseDischarge.fs` checks the lambda against the member's type without the contexts it needs besides, and records those as used there. Anywhere else, such as `def inst = Log(log = ...)`, it is `NYX0227`, whose message says to write the contexts on the member.

The prelude's higher-order functions take a context variable ([#801](https://github.com/nanyx-lang/nanyx/issues/801)): `list.map: [c] list(a), ([c] a -> b) -> list(b)`, `memory: [c] ([$Memory & c] () -> a) -> a`, and the builder `proc`'s `try`. A function type that is not a parameter or result of another, such as a record field's, binds its own context variables.

How it differs from the design above, for now:

- **A lambda's body is checked against the contexts where it is written,** so `withConnection { insert(...) }` in a def that doesn't declare `Connection` is an error at `insert`.
- **A parameterised context is never carried,** even at a type variable: it is found where the lambda is made, as before.
- **A lambda written inside a `use` of a record written out doesn't require a context:** `use (bar = ...) in { bar("x") }` is a `() -> string`, since the record names no context, and the lambda keeps the member it captured where it was made. A `use` of a value whose type names the context, `use Foo(bar = ...) in { bar("x") }`, gives the lambda `[Foo] () -> string` ([#844](https://github.com/nanyx-lang/nanyx/issues/844)): the `use` binds each member with the symbol `Foo` declared it with, as a def that declares `[Foo]` does.
- **A call reads what a context variable stands for from the types solved so far,** and lambda parameters take their types from the constraints only while they all agree, so an earlier type error can leave a context unbound or a parameter's type unknown.

In the Wasm backend ([#836](https://github.com/nanyx-lang/nanyx/issues/836)), a function value whose type requires contexts takes their members as parameters after its others, and each call passes the ones in scope there: the IR type `IrFunction` lists them, by key, contexts sorted by name and each context's members in its layout's order. So:

- A lifted lambda takes the members of the contexts its type requires as parameters. It still captures the members of the others its scope supplies, those of a parameterised context found where it is made: those of the def around it, by their parameters, and those each `use` in scope loads that it may call, by the locals lambda lifting evaluates the `use`'s bindings into, which it loads again with a `use` of its own ([#778](https://github.com/nanyx-lang/nanyx/issues/778)).
- A top-level function named as a value is a closure over a wrapper that takes the members its value's type requires and captures the others, found where it is named.
- A def with context variables, such as `[c]` in `run: [c] ([c] () -> string) -> string`, is specialised on the members of the contexts they stand for at each call (`CoreIrLower.callee`), and takes those members after the ones it declares.
- A function value given where one requiring other contexts is expected, as an argument, a context member, a `use` binding, a list element or a def's result, is adapted (`FunctionLowering.Coerce`): a closure over an adapter that holds it, drops the members it doesn't take and captures the ones it needs that the expected type doesn't pass, as they are where it is given. That is how `use (write = { s -> write(...) })` calls the outer `write`, and how a builder's `try`, whose context variable no call binds, takes a continuation that prints.

So a lambda that uses a context may leave the scope that supplies it, and a function may be named as a value where its contexts aren't available: the type checker's `NYX0225` and the check from [#773](https://github.com/nanyx-lang/nanyx/pull/773), which stopped both because the backend once captured the members, are gone. A lambda that leaves still requires the context where it is called, as its type says.

# Design notes

- Prefer wording like "requires context" over "can only exist".
- Context requirements should be tracked uniformly by type inference and assignability.
- Escape analysis for mutable values then falls out of standard type checking.
