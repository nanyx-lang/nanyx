---
title: "Context calls"
description: "What a call to a function that requires contexts costs, and how the compiler can make it cheaper"
order: 25
---

> **Status: proposed.** This page describes what a call to a function that requires contexts compiles to today, and
> three changes that would make it cheaper. None of them is done. It is part of the [compilation](./compilation)
> workstream, and its issues carry the `compilation`, `wasm backend` and `performance` labels.

# Summary

A function that requires contexts takes each of their members as a closure, and calls them through the function
table. Most calls pass members that are known when the call is compiled: the ambient `Console`, or an instance built
from attached definitions, whose members are top-level functions. The compiler can use that. A closure over a
top-level function can be static data, so passing one allocates nothing. A function can be specialised on the
members known at its call, so it calls them directly and is not passed the ones it never reads. An inliner could then
fold small functions such as `println` into their callers.

# What a call costs today

Since printing takes any value with a `ToString` ([Printing values](./printing-values#printing)), `println` is an
ordinary function, `[Console & ToString(a)] a -> ()`. Hello world compiles to this (from
`Nanyx.Compiler.Wasm.Tests/E2E/twentythree/expected.wat`):

- **The call site allocates a closure for every member.** `println("Hello, Nanyx!")` makes six closures on the heap,
  one for each of `Console`'s five members and one for `string.toString`, and drops them after the call. Each is a
  closure over the wrapper of a top-level function (`ContextArgument.ClosureOverWrapper` in `CoreIrLowerInput.fs`) and
  captures nothing, so every one of them is the same block every time.
- **The callee calls its members indirectly.** `println__string` takes the six closures as parameters, after its
  own. It reads two of them, `write` and `toString`, and makes three `call_indirect`s.
- **The module carries what makes this work.** It has a wrapper for each member (`__fn_write`), a table to call
  them through, and a drop and a dup function for each member's function type. Hello world's WebAssembly text grew by
  about 290 lines with #739.

How members are passed is described under Monomorphisation in [Memory management](./memory-management).

## Functions that require contexts as values

A top-level function that requires contexts can be passed as a value where they are all available, as in
`[1, 2] \list.map(println)` or `apply(say, "hi")`. It is passed as a closure over its wrapper that captures the members
found where it is named, and the wrapper calls the function with its arguments and them
(`Lowering.WrapperCalling` in `CoreIrLower.fs`). The members are found as a call finds them: the type checker records
where a function named as an argument of a call finds each parameterised context, once that call has settled its
type (`resolveReferenceInstances` in `Infer.fs`), so `println` at `int` is passed `ToString(int)`, and at a type
variable `a` of a function that requires `ToString(a)`, that function's member. A context it requires at a type left
open where it is named, with no instance in scope for it, cannot be compiled yet
([#599](https://github.com/nanyx-lang/nanyx/issues/599)), nor can a function that requires a parameterised context
named as a value anywhere other than an argument of a call, such as `def f = println`
([#807](https://github.com/nanyx-lang/nanyx/issues/807)).

When function types carry contexts ([#768](https://github.com/nanyx-lang/nanyx/issues/768)), a value of type
`[c] a -> b` will be passed its members where it is called instead. Such a value can be a closure over a wrapper that
takes the members as parameters after its others, rather than from its environment; a value of a type with no
contexts stays a closure over the members where it is named, as now.

# Design

The three changes are independent, and each is worth doing without the others. They are listed in the order to do
them.

## Closures over top-level functions are static data

A closure over a top-level function captures nothing, so it can be laid out once in the data segment, as a closure
on the heap is laid out, with a reference count of 0. String literals already work this way: `retain` and `release`
leave a count of 0 alone, so dup and drop need no change and the closure is never freed. A call site passes a
constant address.

This is not specific to contexts. A top-level function passed as a value, as in `list.map(xs, double)`, gets the same
treatment. ([#743](https://github.com/nanyx-lang/nanyx/issues/743))

## Specialising on known members

The monomorphiser already specialises a generic definition on its type arguments (`println__string`). It can
specialise a definition that requires contexts on its context members too:

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

Hello world's `println__string` then calls `string.toString` and `write` directly, and its call passes only the
value. The cost is code size: a definition called with several instances has a specialisation for each, which is the
trade monomorphisation already makes for types. ([#744](https://github.com/nanyx-lang/nanyx/issues/744))

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

1. Closures over top-level functions are static data
   ([#743](https://github.com/nanyx-lang/nanyx/issues/743)).
2. Specialise a function on the context members known where it is called
   ([#744](https://github.com/nanyx-lang/nanyx/issues/744)). Its code goes in a new file: `CoreIrLower.fs` is at the
   1,500-line limit.
3. Inline small functions in the core IR, once the open questions are settled
   ([#745](https://github.com/nanyx-lang/nanyx/issues/745)).
