---
title: "Context calls"
description: "What a call to a function that requires contexts costs, and how the compiler can make it cheaper"
order: 25
---

> **Status: in progress.** This page describes what a call to a function that requires contexts compiles to, and
> three changes that make it cheaper. Static closures and specialising on known members are done; inlining is not. It
> is part of the [compilation](./compilation) workstream, and its issues carry the `compilation`, `wasm backend` and
> `performance` labels.

# Summary

A function that requires contexts takes each of their members as a closure, and calls them through the function
table. Most calls pass members that are known when the call is compiled: the ambient `Console`, or an instance built
from attached definitions, whose members are top-level functions. The compiler can use that. A closure over a
top-level function is static data, so passing one allocates nothing. A function can be specialised on the
members known at its call, so it calls them directly and is not passed the ones it never reads. An inliner could then
fold small functions such as `println` into their callers.

# What a call costs

Since printing takes any value with a `ToString` ([ToString(a)](../advanced/contexts#tostringa)), `println` is an
ordinary function, `[Console & ToString(a)] a -> ()` ([#726](https://github.com/nanyx-lang/nanyx/issues/726)), that
writes `toString(value)` through `Console`'s `write`. The printing functions are not members of `Console` because a
context member can't be generic with a requirement of its own: a member is passed as a closure, and a closure has no
`ToString` to take. Before specialising on known members
([#744](https://github.com/nanyx-lang/nanyx/issues/744)), hello world compiled to this:

- **The call site passed a closure for every member.** `println("Hello, Nanyx!")` passed six closures, one for each
  of `Console`'s five members and one for `string.toString`. Each was a closure over the wrapper of a top-level
  function (`ContextArgument.ClosureOverWrapper` in `CoreIrLowerInput.fs`) and captured nothing. Static closures
  ([#743](https://github.com/nanyx-lang/nanyx/issues/743)) made them six constant addresses; before that, the call
  made the six on the heap and dropped them after the call.
- **The callee called its members indirectly.** `println__string` took the six closures as parameters, after its
  own. It read two of them, `write` and `toString`, and made three `call_indirect`s.
- **The module carried what made this work.** It had a wrapper for each member (`__fn_write`), a table to call
  them through, and a drop and a dup function for each member's function type: about 290 lines of WebAssembly text.

Now every one of those members is known where `println` is called, so the call passes only the value, to
`println__string__known`, which calls `string.toString` and `write` directly
(`Nanyx.Compiler.Wasm.Tests/E2E/twentythree/expected.wat`). A member that is not known is still passed as a closure,
and costs what it did. How members are passed is described under Monomorphisation in
[Memory management](./memory-management).

## Functions that require contexts as values

A top-level function that requires contexts can be passed as a value where they are all available, as in
`[1, 2] \list.map(println)` or `apply(say, "hi")`. Its value's type requires its contexts without parameters, such as
`Console`, those a def that doesn't write its type was inferred to require among them (`carryPlainContexts` in
`CodeGen.fs`), and each call passes their members, as it does to any function value whose type requires contexts
([#836](https://github.com/nanyx-lang/nanyx/issues/836), see [below](#function-values-that-require-contexts)). It is
passed as a closure over its wrapper
that takes those members after its other parameters and captures the members of its parameterised contexts, found
where it is named; the wrapper calls the function with its arguments and them all
(`Lowering.WrapperCalling` in `CoreIrLower.fs`). The captured members are found as a call finds them: the type checker records
where a function named as an argument of a call, by its name or qualified (`math.max`), finds each parameterised
context, once that call has settled its type (`resolveReferenceInstances` in `ContextInstances.fs`), so `println` at
`int` is passed `ToString(int)`, `math.max` passed to `fold` over a `list(int)` is passed `Ord(int)`, and at a type
variable `a` of a function that requires `ToString(a)`, that function's member. So is a function named point-free as
a def's value, at the type the def writes (`resolveValueInstances`): `def g: [Console] int -> () = println`. Such a
def is not run in the contexts it writes, as a lambda is: its value is a function that requires them, so they are
part of its type, as they are of `def k = say` (`Contexts.extractDefContexts`, which the backend reads too). A context
it requires at a type left open where it is named, with no instance in scope for it, cannot be compiled yet
([#599](https://github.com/nanyx-lang/nanyx/issues/599)), nor can a function that requires a parameterised context
named as a value anywhere else whose type is not settled there, such as `def f = println`
([#807](https://github.com/nanyx-lang/nanyx/issues/807)).

A function with a context variable, such as `apply: [c] ([c] a -> b), a -> b`, is specialised on the members of the
contexts the variable stands for at each call, which it takes after its other parameters and passes to the function
value it calls. So `apply(say, "hi")` and `apply(double, 2)` call two specialisations of `apply`, one of which takes
`Console`'s members.

### Function values that require contexts

A function value whose type requires contexts ([Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts))
takes their members as parameters after its others, and each call passes the ones in scope there: the IR type
`IrFunction` lists them, by key, contexts sorted by name and each context's members in its layout's order
([#836](https://github.com/nanyx-lang/nanyx/issues/836)). So:

- A lifted lambda takes the members of the contexts its type requires as parameters. It still captures the members of
  the others its scope supplies, those of a parameterised context found where it is made: those of the def around it
  that it may call, by their parameters, and those each `use` in scope loads that it may call, by the locals lambda
  lifting evaluates the `use`'s bindings into, which it loads again with a `use` of its own
  ([#778](https://github.com/nanyx-lang/nanyx/issues/778)). It may call a member it names, by its key or its own name,
  in its body or a lambda inside it, and every member when it names a function that requires contexts
  ([#934](https://github.com/nanyx-lang/nanyx/issues/934)).
- A top-level function named as a value is a closure over a wrapper that takes the members its value's type requires
  and captures the others, found where it is named.
- A def with context variables, such as `[c]` in `run: [c] ([c] () -> string) -> string`, is specialised on the
  members of the contexts they stand for at each call (`CoreIrLower.callee`), and takes those members after the ones
  it declares.
- A function value given where one requiring other contexts is expected, as an argument, a context member, a `use`
  binding, a list element or a def's result, is adapted (`FunctionLowering.Coerce`): a closure over an adapter that
  holds it, drops the members it doesn't take and captures the ones it needs that the expected type doesn't pass, as
  they are where it is given. That is how `use (write = { s -> write(...) })` calls the outer `write`, and how a
  builder's `try`, whose context variable no call binds, takes a continuation that prints.

So a lambda that uses a context may leave the scope that supplies it, and a function may be named as a value where
its contexts aren't available. A lambda that leaves still requires the context where it is called, as its type says.

# Design

The three changes are independent, and each is worth doing without the others. They are listed in the order to do
them.

## Closures over top-level functions are static data

Done ([#743](https://github.com/nanyx-lang/nanyx/issues/743)). A closure over a top-level function captures nothing,
so it is laid out once in the data segment, as a closure on the heap is laid out, with a reference count of 0. String
literals work this way too: `retain` and `release` leave a count of 0 alone, so the closure is never freed. A call
site passes a constant address.

This is not specific to contexts. Lowering makes every closure that captures nothing an atom, `AStaticClosure`
(`closureOver` in `CoreIr.fs`), rather than a `MakeClosure`: a context member passed at a call, a top-level function
passed as a value (`list.map(xs, double)`), and a lambda that captures nothing, which lambda lifting made a top-level
function. An atom is never a variable, so reference counting gives it no dup or drop, as it gives a string literal
none. The module lays out one block for each such function after its string literals (`staticClosureData` in
`CodeGen.fs`): a count of 0, an info word of 0, since nothing ever drops it, and the function's table slot. The tests
are in `Features/Memory/StaticClosureTests.fs`.

In the `proc` measurement on [Context escape](./context-escape#measured), variant C goes from 9 allocations per block
to 7, as predicted.

## Specialising on known members

The monomorphiser specialises a generic definition on its type arguments (`println__string`), and a definition that
requires contexts on its context members too (`CoreIrLowerKnown.fs`):

- A specialisation's key is the definition, its type arguments, and, for each context member, either the top-level
  function it is known to be or "unknown". A member is known when the call finds it as a closure over a top-level
  function's wrapper (the ambient instance, an instance built from attached definitions) or as a top-level
  definition's value.
- In a specialisation, a known member is not a parameter. A call to it is a direct `call` or `return_call`. A call that
  passes it on passes it as known, so the callee is specialised on it too.
- A member that is not known stays a closure parameter, as today: a `use` of a lambda that captures locals, or a member
  that a function not specialised on it passes on.
- A known member that the specialisation never reads is not passed.
- Specialisations on members count towards the limit on specialisations per definition (`maxSpecialisations`).
- Debug and release builds specialise alike, since specialising changes no behaviour.

Hello world's `println__string__known` calls `string.toString` and `write` directly, and its call passes only the
value. The cost is code size: a definition called with several instances has a specialisation for each, which is the
trade monomorphisation already makes for types. A definition that is not generic is still lowered as it is, taking
all its members, and exported; a specialisation of it on known members is named with `__known`
(`greet__known`), and is not exported.

A def that doesn't write its type and infers an ambient context, such as `def greet = { name -> println(name) }`,
takes the context's members as one that writes `[Console]` does
([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)). At a script's top level the members are the ambient
instance's, so the call is to `greet__known`, which calls them directly; the unspecialised `greet` is lowered and
exported too, with the drop and dup functions of its members' types. An `@entry` is called by its host's `run`, with the
members of the `Console` the host `use`s, which are not known there: so `println` in an entry calls the host's `write`
through the table.

How it is built:

- A call works out which of the members it finds are known (`FunctionLowering.KnownMembers`, from what
  `LowerInput.ContextArguments` finds) before it lowers its arguments, and asks for the callee by a key that names them
  (`SpecKey.Known`). It passes the others as before.
- The specialisation binds each known member to a local, not a parameter. A call through that local is a direct call to
  the function the member wraps (`CallValue`), with its arguments arranged as the wrapper would arrange them
  (`directArguments`). A call that passes the local on to a function that requires the context finds it known, so the
  callee is specialised on it too.
- Anywhere else the local is read, it is a value: a lambda captures it, or it is passed to `list.map`. Only then is
  the local given the member's value, a closure over the wrapper or the top-level value, at the top of the function
  (`WithKnownValues`, which looks for the locals the body names with `usedOf`).

([#744](https://github.com/nanyx-lang/nanyx/issues/744))

This is what Rust does for generic functions, as against `dyn` trait objects, and what GHC's specialisation does for
type class dictionaries.

## Inlining

The backend has no inliner. After specialising, `println("hi")` is a call to a function that calls
`string.toString`, which returns its argument, and calls `write` twice. Inlining would fold that into the caller.

An inliner would run on the core IR before reference counting (`CoreIrCounting.fs`) and reuse, so it never has to
cancel the dup and drop pairs it would otherwise leave behind. It replaces a direct call to a small function that does
not call itself with the function's body, then simplifies what that exposes: a call through a closure known at that
point becomes a direct call, and a closure nothing uses any more is not made. With that simplification, inlining alone
would remove much of what specialising removes, but only where it inlines.
([#745](https://github.com/nanyx-lang/nanyx/issues/745))

# Open questions

These are about inlining, which waits on them; [#745](https://github.com/nanyx-lang/nanyx/issues/745) is labelled
`needs decision`.

- **Whether to inline at all, for now.** Static closures and specialising remove most of the cost. What is left is a
  direct call per member, which WebAssembly engines often inline themselves.
- **Which calls to inline.** A limit on the size of the inlined body, functions with one caller, functions marked by an
  attribute, or a mix.
- **Debug builds.** Inline in every build, or only with `--release`. Inlining moves where a trap happens into the
  caller, which changes what a debug build reports.
- **Tail calls.** Every call in tail position is a tail call ([Tail calls](./tail-calls)). An inlined body's tail calls
  must stay in tail position in the caller, and a function must not be inlined into itself.

# Plan

1. ~~Closures over top-level functions are static data~~
   ([#743](https://github.com/nanyx-lang/nanyx/issues/743), done).
2. ~~Specialise a function on the context members known where it is called~~
   ([#744](https://github.com/nanyx-lang/nanyx/issues/744), done: `CoreIrLowerKnown.fs`).
3. Inline small functions in the core IR, once the open questions are settled
   ([#745](https://github.com/nanyx-lang/nanyx/issues/745)).
