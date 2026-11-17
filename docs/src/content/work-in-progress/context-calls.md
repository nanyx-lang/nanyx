---
title: "Context calls"
description: "What a call to a function that requires contexts costs, and how the compiler can make it cheaper"
order: 25
---

This page says what a call to a function that requires contexts compiles to. What a user sees, that a function's
contexts are part of its type and passed where it's called, is in [Contexts](../advanced/contexts). It is part of the
[compilation](./compilation) workstream, and its issues carry the `compilation`, `wasm backend` and `performance`
labels. One thing is still to do: a `use` of a lambda that captures nothing is still passed as a closure (see
[Still to do](#still-to-do)).

# Summary

A function that requires contexts takes each of their members as a closure, and calls them through the function
table. Most calls pass members that are known when the call is compiled: the ambient `Console`, or an instance built
from attached definitions, whose members are top-level functions. So a closure over a top-level function is static
data, and passing one allocates nothing. A function is specialised on the members known at its call, so it calls them
directly and is not passed the ones it never reads. The [inliner](./inlining) then folds small functions such as
`println` into their callers.

`println` is an ordinary function, `[Console, ToString(a)] a -> ()`, that writes `toString(value)` through `Console`'s
`write`; it is not a member of `Console` because a context member can't be generic with a requirement of its own: a
member is passed as a closure, and a closure has no `ToString` to take. Every member it needs is known where it is
called, so the call passes only the value, to `println__string__known`, which calls `string.toString` and `write`
directly. The inliner then folds all of it into the caller: hello world's start function writes the string itself,
with no function table and no closures (`Nanyx.Compiler.Wasm.Tests/E2E/twentythree/expected.wat`). A member that is
not known is passed as a closure over the wrapper of a function, and called with `call_indirect`.

## Functions that require contexts as values

A top-level function that requires contexts can be passed as a value where they are all available, as in
`[1, 2] \list.map(println)` or `apply(say, "hi")`. Its value's type requires its contexts without parameters, such as
`Console`, those a def that doesn't write its type was inferred to require among them (`carryPlainContexts` in
`CodeGen.fs`), and each call passes their members, as it does to any function value whose type requires contexts
([#836](https://github.com/nanyx-lang/nanyx/issues/836), see [below](#function-values-that-require-contexts)). It is
passed as a closure over its wrapper
that takes those members after its other parameters and captures the members of its parameterised contexts, found
where it is named; the wrapper calls the function with its arguments and them all
(`Lowering.WrapperCalling`, lowered in `CoreIrLowerLifted.fs`). The captured members are found as a call finds them: the type checker records
where a function named as an argument of a call, by its name or qualified (`math.max`), finds each parameterised
context, once that call has settled its type (`resolveReferenceInstances` in `ContextInstances.fs`), so `println` at
`int` is passed `ToString(int)`, `math.max` passed to `fold` over a `list(int)` is passed `Ord(int)`, and at a type
variable `a` of a function that requires `ToString(a)`, that function's member. So is a function named point-free as
a def's value, at the type the def writes (`resolveValueInstances`): `def g: [Console] int -> () = println`. Such a
def is not run in the contexts it writes, as a lambda is: its value is a function that requires them, so they are
part of its type, as they are of `def k = say` (`Contexts.extractDefContexts`, which the backend reads too). A context
it requires at a type nothing constrains, as `dbg(twice)` names `twice: [Show(a)] a -> string`, needs no instance:
when a parameter of the value is of such a type, the value is never called, since no value of that type is ever made,
so the closure captures a stand-in for each such member, which crashes (`CoreIrLowerInstances.neverCalled`,
[#599](https://github.com/nanyx-lang/nanyx/issues/599)). A def that doesn't write its type and names such a function
point-free, as `def f = println` does, is read as the lambda that calls it, `{ __p0 -> println(__p0) }`
(`ContextInstances.pointFreeAsLambda`, recorded in `ReadAs`), so it requires the context at its own type as any
function that calls one does ([below](#function-values-that-require-contexts),
[#807](https://github.com/nanyx-lang/nanyx/issues/807)). At the top level it is then an unannotated function, which
infers the ambient contexts it uses, not a script's value ([#1389](https://github.com/nanyx-lang/nanyx/issues/1389)).

A function with a context variable, such as `apply: [c] ([c] a -> b), a -> b`, is specialised on the members of the
contexts the variable stands for at each call, which it takes after its other parameters and passes to the function
value it calls. So `apply(say, "hi")` and `apply(double, 2)` call two specialisations of `apply`, one of which takes
`Console`'s members.

A name that a member in scope has calls that member, unless the type checker found it to name one of the module's own
top-level defs, declared in the file the name is written in (`LowerInput.TopLevelReferences`): a script's `def write = ...` shadows `Console`'s `write`, and
its calls run the def, though the script's statements run in `Console`
([#1278](https://github.com/nanyx-lang/nanyx/issues/1278)).

### Function values that require contexts

A function value whose type requires contexts ([Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts))
takes their members as parameters after its others, and each call passes them: the IR type `IrFunction` lists them,
by key, contexts without parameters sorted by name, then instances of parameterised ones sorted by key, and each
context's members in its layout's order ([#836](https://github.com/nanyx-lang/nanyx/issues/836)). A context without
parameters is passed as it is in scope where the call is. A parameterised one is required at the types the value is
called with, and its members are keyed by that instance (`Show_Point_show` for `show` of `Show(Point)`): each call
passes the instance the type checker found for it there, as a call to a top-level function does
([#1368](https://github.com/nanyx-lang/nanyx/issues/1368)). So:

- A lifted lambda takes the members of the contexts its type requires as parameters. It still captures the members of
  the others its scope supplies, those of a parameterised context found where it is made: those of the def around it
  that it may call, by their parameters, and those each `use` in scope loads that it may call, by the locals lambda
  lifting evaluates the `use`'s bindings into, which it loads again with a `use` of its own
  ([#778](https://github.com/nanyx-lang/nanyx/issues/778)). It may call a member it names, by its key or its own name,
  in its body or a lambda inside it, and every member when it names a function that requires contexts
  ([#934](https://github.com/nanyx-lang/nanyx/issues/934)).
- A local function that requires a parameterised context, written, as in
  `def f: [Show(b)] b -> string = { x -> describe(x) }`, or inferred, as for `def f = { x -> describe(x) }`
  ([#550](https://github.com/nanyx-lang/nanyx/issues/550)), takes the context's members as a lambda takes those of a
  context without parameters. Its body calls a member by the key of the context as its type writes it, or by the
  member's own name, as a top-level function's does, where no other context it requires has a member of that name
  (`CoreIrLowerInstances.aliases`, [#1574](https://github.com/nanyx-lang/nanyx/issues/1574)). The def may be in one that writes its type or not, and its value may run in a context
  without parameters too, as `def f = { x -> println(x) }` runs in `Console`
  ([#807](https://github.com/nanyx-lang/nanyx/issues/807)). Passed as an argument, as in `xs \list.map(f)`, it is read
  as the lambda that calls it, `{ __p0 -> f(__p0) }`, once the call has settled its type, so that the lambda's call
  finds the instance there (`localFunctionArguments` in `Infer.fs`,
  [#1390](https://github.com/nanyx-lang/nanyx/issues/1390)). The type may be settled as a variable of the def around
  it, which then requires the context, as `apply(f, v)` does at `v`'s type; at a type left open otherwise it is passed
  as it is, as `dbg(f)` does. A lambda that names such a function captures the members of the def around it, as one
  that names a top-level function that requires contexts does (`requiresContexts` in `CodeGen.fs`,
  [#1397](https://github.com/nanyx-lang/nanyx/issues/1397)). The type checker keeps such a context out of the function's type (it records it beside
  it, `TypedModule.ContextTypes`), so the backend puts it into the type of the function's lambda, in the lambda's own
  type variables (`localContexts` in `CodeGen.fs`). A function generic in variables of its own has a closure for each
  type it is used at (`GenericLocal`), each taking the members of the instances at that type. A call to the function
  finds each context where the type checker found it at the call, by its place among those the type writes
  (`CoreIrLowerInstances.foundAt`): an instance built from attached definitions (one whose single member requires
  contexts of its own, as `Queue.==` requires `Eq(a)`, is an `InstanceDerived` whose instances the type checker finds
  as for a call to the definition, `ContextInstances.synthesizeInstance`,
  [#1668](https://github.com/nanyx-lang/nanyx/issues/1668)), one derived from a type's parts, or
  one in scope, such as the def around the call's own or a `use`'s. In its body, the function has each member by the
  key the type writes too (`Show_b_show`), or that the type checker writes for one it inferred
  (`TypedModule.LocalContexts`), which the calls in it find it by. Inlining makes these members direct calls
  where they are known, as it does any closure's.
- A top-level function named as a value is a closure over a wrapper that takes the members its value's type requires
  and captures the others, found where it is named.
- A def with context variables, such as `[c]` in `run: [c] ([c] () -> string) -> string`, is specialised on the
  members of the contexts they stand for at each call (`CoreIrLower.callee`), and takes those members after the ones
  it declares.
- A function value given where one requiring other contexts is expected, as an argument, a context member, a `use`
  binding, a list element or a def's result, is adapted (`CoreIrLowerMembers.coerce`): a closure over an adapter that
  holds it, drops the members it doesn't take and captures the ones it needs that the expected type doesn't pass, as
  they are where it is given. That is how `use Console(write = { s -> write(...) })` calls the outer `write`, and how a
  builder's `try`, whose context variable no call binds, takes a continuation that prints.
- A member of a context without parameters that is generic in type variables of its own (`ret: a -> #some(a) | r`)
  is made once, at its `use`, and called at many types, so its context's layout has the values of those variables
  boxed (`IrBox`: a closure whose function returns the value), and so does the lambda a `use` binds to it. A call
  boxes what it passes and unboxes what it is given back, rebuilding a tag's cell, a record or a list around the parts
  it converts, and a function passed to the member or returned by it is adapted the same way (`CoreIrLowerBoxes`,
  [#1160](https://github.com/nanyx-lang/nanyx/issues/1160)).

So a lambda that uses a context may leave the scope that supplies it, and a function may be named as a value where
its contexts aren't available. A lambda that leaves still requires the context where it is called, as its type says.

# Design

## Closures over top-level functions are static data

A closure over a top-level function captures nothing,
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

## Specialising on known members

The monomorphiser specialises a generic definition on its type arguments (`println__string`), and a definition that
requires contexts on its context members too (`CoreIrLowerKnown.fs`):

- A specialisation's key is the definition, its type arguments, and, for each context member, either what it is known
  to be (a top-level function, a top-level value or a constant) or "unknown". A member is known when the call finds it
  as a closure over a top-level function's wrapper (the ambient instance, an instance built from attached
  definitions), as a top-level definition's value, or bound by a `use` to a top-level function that is not generic and
  requires no contexts, or to an int or bool literal: `use Counter(bump = addThree, limit = 1000003)` makes both
  members known ([#1299](https://github.com/nanyx-lang/nanyx/issues/1299)). A `use` of a generic function, of a cell's
  value or of any other expression leaves the member unknown.
- In a specialisation, a known member is not a parameter. A call to it is a direct `call` or `return_call`. A call that
  passes it on passes it as known, so the callee is specialised on it too.
- A member that is not known stays a closure parameter: a `use` of a lambda that captures locals, or a member
  that a function not specialised on it passes on.
- A known member that the specialisation never reads is not passed.
- Specialisations on members count towards the limit on specialisations per definition (`maxSpecialisations`).
- Debug and release builds specialise alike, since specialising changes no behaviour.

Hello world's `println__string__known` calls `string.toString` and `write` directly, and its call passes only the
value. The cost is code size: a definition called with several instances has a specialisation for each, which is the
trade monomorphisation already makes for types. A specialisation on known members is named with `__known`
(`greet__known`). A definition that declares contexts is not exported, since no host could pass it the members, and
its own form, which takes all its members, is lowered only where a call asks for it: a function value, or a caller
that does not know the instance (`CoreIrLower.calleeKnowing`). So each such definition is emitted once for each way
it is called, not as an exported plain copy beside its `__known` one
([#1269](https://github.com/nanyx-lang/nanyx/issues/1269)).

A def that doesn't write its type and infers an ambient context, such as `def greet = { name -> println(name) }`,
takes the context's members as one that writes `[Console]` does
([#1245](https://github.com/nanyx-lang/nanyx/issues/1245)). An `@entry`, and a script's statements, are called by the
host's `run` with the members of the `Console` the host `use`s. The host's `run` is inlined into the start function,
so the closures over those members are known there, and the simplifier and the inliner turn `println`'s call of
`write` into a direct one ([#1469](https://github.com/nanyx-lang/nanyx/issues/1469), see [Inlining](./inlining)).

How it is built:

- A call works out which of the members it finds are known (`CoreIrLowerMembers.knownMembers`, from what
  `LowerInput.ContextArguments` finds) before it lowers its arguments, and asks for the callee by a key that names them
  (`SpecKey.Known`). It passes the others as before.
- The specialisation binds each known member to a local, not a parameter. A call through that local is a direct call to
  the function the member wraps (`CallValue`), with its arguments arranged as the wrapper would arrange them
  (`directArguments`). A call that passes the local on to a function that requires the context finds it known, so the
  callee is specialised on it too.
- Anywhere else the local is read, it is a value: a lambda captures it, or it is passed to `list.map`. Only then is
  the local given the member's value, a closure over the wrapper or the top-level value, at the top of the function
  (`WithKnownValues`, which looks for the locals the body names with `usedOf`).
- Lambda lifting turns each binding of a `use` into a local `def`, so a `use`'s member is a local. A `def` whose value
  is a top-level function named as a value, a literal, or a local known already records its local as known
  (`FunctionLowering.NoteDefinedKnown`): such a local never changes, as a cell is a `def` of `mut(...)`. A call that
  finds a member in scope in such a local finds it known, and a call through the local is a direct call.

This is what Rust does for generic functions, as against `dyn` trait objects, and what GHC's specialisation does for
type class dictionaries.

After specialising, the [inliner](./inlining) folds small functions such as `println` into their callers, and makes
the members of a `use` it brings into the same function as their calls known.

# Still to do

- **A `use` of a lambda that captures nothing** is still passed as a closure. The callee could be specialised on the
  lifted function and passed only its environment.
