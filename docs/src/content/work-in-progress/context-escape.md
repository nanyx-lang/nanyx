---
title: "Context escape"
description: "Which values may leave the scope that supplies a context, and how a use implements one context in terms of another"
order: 1
---

This page settles two related questions, now that function types carry contexts
([#768](https://github.com/nanyx-lang/nanyx/issues/768)): how a `use` can implement one context in terms of another,
and what replaces NYX0224, the escape check for `memory` blocks. The design was decided in
[#1035](https://github.com/nanyx-lang/nanyx/issues/1035). The plan at the end lists the issues and which are done.

NYX0225, the escape check for other contexts, is gone: since [#836](https://github.com/nanyx-lang/nanyx/issues/836)
each call passes a function value the instances in scope where it is called, so a lambda that requires a context may
leave the scope that supplies it.

# A function type never hides a context

`string -> ()` promises that the function has no effects, as the [principles](../foundations/principles#separation-of-pure-and-impure-code)
say a pure function does. A requirement can move from a lambda to the place that takes it in, but it never
disappears. A lambda doesn't capture the contexts it uses: it requires them where it is called. Koka and Flix work the
same way. Neither lets a closure capture an effect and drop it from its type.

So there is no distinction between "static" contexts such as `Console` and "instance" ones such as `$Memory` in the
type system. Capturing the one `Console` or being passed it at each call makes no difference to what a program does,
which leaves it as an optimisation for the backend ([Context calls](./context-calls)).

A lambda returned from a function keeps its contexts in its type, as in
[#774](https://github.com/nanyx-lang/nanyx/issues/774):

```nanyx
def mk: [Console] () -> (string -> ()) = {
  { m -> println(m) }   -- NYX0227: write `[Console] string -> ()`
}
```

A higher-order function's context variable is unchanged too: `list.map(xs, println)` binds `c` to `Console`.

# Discharging one context into another

A `use` may give a context's member a lambda that needs more contexts than the member's type declares. The extra
contexts are required where the `use` is written, as a Koka or Flix handler's clauses add their effects to the place
where the handler is installed:

```nanyx
context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}

@entry
def main = {
  use Log(log = { message -> println(message) })   -- main requires Console here
  greet("world")
}
```

- **The member's type stays as declared.** `log` is `string -> ()`, so `greet` knows nothing of `Console`, and
  `message` takes `string` from it.
- **The member runs with the instances in scope at the `use`.** `greet` has no `Console` to pass it. So
  `use Log(log = { m -> log("[app] {m}") })` calls the outer `Log`.
- **Only an instance written at the `use` itself.** An instance built as a value has nowhere to say what it needs, so
  `def inst = Log(log = { m -> println(m) })` stays NYX0227. Values that carry contexts (`[Console] Log`) are an open
  question below.
- **A reusable handler is a function,** as it is in Koka:

  ```nanyx
  def withConsoleLog: [Console & c] ([Log & c] () -> a) -> a = { body ->
    use Log(log = { m -> println(m) })
    body()
  }
  ```

- **The backend needs nothing new.** `FunctionLowering.Coerce` already adapts a function value given where one that
  passes fewer members is expected, capturing the members it needs where it is given. A `use` binding goes through it.

`UseDischarge.fs` does this in the type checker ([#1054](https://github.com/nanyx-lang/nanyx/issues/1054)). A `use`
marks the instance it loads; the constructor call checks each lambda it is given against its member's type without
the contexts the lambda needs besides, and records those as used where the `use` is written, so a lambda around the
`use` requires them too. The lambda's parameters take their types from the member wherever the constructor is called.

A `use` of a record written out, `use (log = { m: string -> println(m) })`, already checks and runs, because its
members have no declared types to fit: `log` is `[Console] string -> ()`, and a lambda that calls it requires
`Console`.

# Escape from a memory block

A lambda over a cell must not leave its `memory` block. NYX0233 rejects this, as NYX0224, a check of the syntax, did
before scoped contexts:

```nanyx
def tryToFool = {
  memory {
    mut y = 0

    { y := y + 1 }
  }
}
```

Requiring `$Memory` isn't enough, because every `memory` block supplies one. The prelude declares
`memory: [c] ([$Memory & c] () -> a) -> a`, and nothing stops `a` being `[$Memory] () -> int`:

```nanyx
def counter = memory { mut y = 0; { y := y + 1; y } }   -- [$Memory] () -> int

def a = memory { counter() }   -- 1
def b = memory { counter() }   -- 2: two pure expressions now differ
```

## Scoped contexts

A context declared `scoped` belongs to the scope that supplies it, and nothing that requires it can leave that scope:

```nanyx
scoped export context $Memory = ()
```

- **Where a scope starts.** A function _supplies_ a context to a parameter when the parameter's type requires it and
  the function's own type doesn't: `memory` supplies `$Memory` to its block. For a scoped context, each call to a
  function that supplies it starts a fresh, rigid scope. Every requirement of the context inside belongs to that
  scope, and the call's result type must not mention it. This is Haskell's `runST`, Koka's `local<h>` and Flix's
  regions, with the scope variable implied by the marker rather than written, so users never see it.
- **Only scoped contexts are restricted.** Every other context keeps "require, don't capture", so
  `withConsoleLog { { log("x") } }` above may return a lambda that requires `Log`.
- **Instances come only from their supplier.** A scoped context must be nominal (`$`), so only its own module can
  construct an instance, and its supplying function is where each instance and scope starts. `use` only loads an
  instance it is given. `scoped context Tx = (...)` is an error. Any module may declare a scoped context, such as a
  `$Transaction` in a database library.
- **A type variable bound outside the scope is safe.** `def run2: (() -> b) -> b = { f -> memory { f() } }` checks:
  `b` can't mention a scope made inside `run2`.
- **It reaches type variables.** `def run = { mk -> memory { mk() } }` passes `mk` the block's `$Memory`, so
  `run(mk)` is rejected where its result would require that block's `$Memory`.
- **It fits discharge at a `use`.** A `use` in a block whose member's lambda changes a cell requires `$Memory` where
  the `use` is written, which is inside the block.

A block can be given its instance, `memory { m -> ... }`. Holding it counts as requiring it, so `memory { m -> m }` is
rejected. `mut` uses the innermost `$Memory` in scope, and `use m1` in an inner block makes `m1` the innermost, so the
inner block can make cells that belong to the outer one:

```nanyx
memory { m1 ->
  memory {
    use m1
    mut z = 0   -- belongs to m1's block
    ...
  }
}
```

`memory`'s block takes the instance, `memory: [c] ([$Memory & c] $Memory -> a) -> a`, and `memory { ... }` still
checks, since a lambda with no parameters ignores one.

## Options considered

- **A lacks constraint for every context** (Option A in earlier drafts): a function's result must not require a
  context it supplies, for any context. It needs no marker, but it also rejects a handler function returning a lambda
  that requires the context it handles, which "require, don't capture" allows. It can't tell instances apart either,
  so a value made in an inner block could never reach an outer one.
- **An explicit scope variable**, `context $Memory(s)` with `forall s` in `memory`'s signature. It's what `scoped`
  means, but users would see `s` in hovers and errors, and it needs rank-2 syntax.
- **Capturing "static" contexts** such as `Console` into a lambda whose type drops them. A pure-looking function could
  then print.
- **Capture sets** in function types, as Effekt and Scala's capture checking have: `string -> () ^{Console}`. More
  expressive, but a second context list on every function type.

# Settled

- **A lambda with no parameters, checked against a type that takes one parameter, ignores it.** With no expected
  type, `{ ... }` still infers `() -> ...`, and `{ () -> ... }` always takes unit. `memory { ... }` and
  `memory { m -> ... }` check against one signature because of it. Done in
  [#813](https://github.com/nanyx-lang/nanyx/issues/813): the syntax tree marks a lambda written `{ () -> ... }`
  (`takesUnit` on `Lambda`), and before the backend reads a module, `ExpressionRewrites.nameIgnoredParameters` gives
  each `{ ... }` whose type takes an input a parameter it ignores. None of the parser's desugarings (`for`, `__with`,
  `__rec`, workflow blocks) makes a lambda without parameters that is checked against a one-parameter type, so they
  accept what they did.
- **`use` loads a `$Memory`; it can't make one.** `use` loads any context instance you have. `$Memory` is nominal, so
  only the standard library can construct one, and `memory` is the only way to get one.

# Proposal: contexts are not types

_Proposed in [#1141](https://github.com/nanyx-lang/nanyx/issues/1141), not yet decided._

A context instance can be built only where a `use` loads it, so it is never a value. Today the rule above (only an
instance written at the `use` discharges) means a handler can't be moved into a function of its own:

```nanyx fragment
def consoleLogger: [Console] () -> Log = {
  Log(log = { m -> println(m) })   -- NYX0227: 'log' is 'string -> ()'
}

def main = {
  use consoleLogger()
  log("hello")
}
```

The proposal draws the line there, and makes every helper a function that takes the code to run:

- **A context is not a type.** A context's name may appear only in a context list (`[Log]`) and as the constructor in
  a `use`. It can't be a parameter's, a result's or a field's type, so `() -> Log` is an error.
- **No def holds an instance.** `def inst = Log(...)` is an error, and so is a function that returns one. A constructor
  call is valid only as the instance of a `use`.
- **A reusable handler is a function that runs a lambda under a `use`.** This is the form the section above calls "a
  reusable handler", and the only one:

  ```nanyx fragment
  def withDefaultLogger = { prefix, f ->
    use Log(log = { msg -> println("[{prefix}] {msg}") }) in f()
  }
  ```

  It takes parameters as any function does, and its members' extra contexts are discharged at its `use`, so
  `withDefaultLogger` requires `Console`.

## Workflows

A workflow builder is a record value today: `proc` in `prelude-results.nyx` is a `def` of a structural record type,
and `proc { ... }` desugars to `proc.apply({ use proc in ... })`. Under the proposal a builder is a declared context
and a function that runs the block under it:

```nanyx fragment
context Proc = (try: ..., return: ..., zero: ..., for: ...)

def proc = { f ->
  use Proc(
    try = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    return = { value -> #some(value) }
    zero = #some()
    for = { items, body, rest -> procFor(items, body, rest) }
  ) in f()
}
```

- **The desugaring is a call.** `proc { ... }` is `proc({ ... })`. Inside the block, `try x`, `return x`, `zero` and
  `for` are rewritten into calls of the members with continuations, as now. The block lambda then requires `[Proc]`,
  which `proc`'s `use` discharges.
- **`apply` and `@workflow` go.** The function's body is what `apply` did, and the helper `@workflow` generates is the
  function itself.
- **Builders take parameters for free.** `retrying(3) { ... }` is `retrying(3, { ... })`, a trailing lambda.
- **A builder may need contexts.** An `async` that needs a scheduler is `def async: [Scheduler] ...`, discharged at its
  `use` as above.
- **`@customKeywordType` moves to the member's declaration** in the `context`, since there is no builder record left
  to put it on.

## What it costs

There is no instance at run time. The backend passes a context's members one by one, as closures, to each function
that requires it ([Context calls](./context-calls)), and a `use` binds each member's lambda to a local. So a `use`
makes one closure per member: four for `Proc`.

- **Allocation.** Members that capture nothing, as `Proc`'s do, are lifted to top-level functions, so each is a
  closure over a top-level function. Today that is a small heap allocation per member, per block. With
  [#743](https://github.com/nanyx-lang/nanyx/issues/743) they become static data and cost nothing.
- **Calls.** Today a block's `use proc` names a top-level definition, so with
  [#744](https://github.com/nanyx-lang/nanyx/issues/744) the block could call `try` directly. Under the proposal the
  members come from `proc`'s `use`, outside the block, so they stay closure parameters of the block and its calls stay
  indirect, unless `proc` is inlined where it is called. A function of the form `use ... in f()` is the smallest case
  for an inliner ([#745](https://github.com/nanyx-lang/nanyx/issues/745)); inlined, the block compiles as it does
  today.
- **The real cost of a workflow is unchanged.** Each `try` makes a continuation closure over the block's live locals
  and calls a member indirectly, in both forms. An experiment compiling `proc` both ways is to measure this.

## What changes

- The prelude's builders (`proc` and any others) become a `context` and a function. The cookbook's `OptionChain` and
  the `@workflow` and `@customKeywordType` docs change with them, and `[typeof optionChain]` goes.
- Named instances become handlers: `def perfect = Move(...)` and `def cheating = Move(...)` in `examples/Nim.nyx`,
  `bobChooses` there, the monoid instances in `examples/traitsAndTypeclasses.nyx`, and `connection` in
  `examples/sql-utils.nyx`.
- [#1137](https://github.com/nanyx-lang/nanyx/issues/1137) (the backend can't compile `use f()` or `use x`) is no
  longer needed: both become type errors.
- [#1059](https://github.com/nanyx-lang/nanyx/issues/1059) is settled for context instances, which never carry
  contexts because they are never values. It stays open for records.

## Questions

- **`memory { m -> ... }`.** The block is given its `$Memory` instance as a value, and `use m` loads it in an inner
  block ([#1058](https://github.com/nanyx-lang/nanyx/issues/1058)). Under the proposal `m` can't be a value. It could
  stay as a name only `use` accepts, as an exception, or the block could be named another way, such as a label.
- **Whether `use` still takes a plain record.** `use (log = { m -> println(m) })` works today. Taking only a declared
  context's constructor gives one answer to what `use` loads.
- **Generic members.** Each `try` in a block has its own `a` and `b`, so `Proc`'s `try` must be polymorphic per call.
  The cookbook's `context OptionChain` declares such a member, but `proc` and the other builders need to check it.

# Open questions

- **Whether a record holding a lambda takes on its contexts**
  ([#1059](https://github.com/nanyx-lang/nanyx/issues/1059)). `(f = { m: string -> println(m) })` would be
  `[Console] (f: string -> ())`, and `Log(log = { m -> println(m) })` would be `[Console] Log`, which `use` would
  require. That is the general design in [Context-qualified types](./context-qualified-types), and it would let an
  instance built as a value discharge a context, as one written at a `use` can. Under the proposal above, context
  instances are never values, and this is about records only.

# Plan

1. ~~Let a `use` give a member a lambda that needs more contexts, required where the `use` is written~~
   ([#1054](https://github.com/nanyx-lang/nanyx/issues/1054)). Done: see `UseDischarge.fs` above.
2. ~~Make a lambda that uses a cell require `$Memory`~~ ([#1055](https://github.com/nanyx-lang/nanyx/issues/1055)).
   Done: a read or change of a cell requires `$Memory` of the lambda around it (`MemoryCells.recordUse`), so
   `{ word -> count := count + 1 }` is `[$Memory] string -> ()`, and a lambda that calls it requires `$Memory` too.
3. ~~Parse and format `scoped context`, which must be nominal~~ ([#1056](https://github.com/nanyx-lang/nanyx/issues/1056)).
   Done: the parser records `Scoped` (with the keyword's span) among the definition's modifiers,
   `TypeDefInfo.IsScoped` carries it, a scoped context that isn't nominal is NYX0232, and `$Memory` is declared
   `scoped`.
4. ~~Keep what requires a scoped context inside the call that supplies it, replacing NYX0224~~
   ([#1057](https://github.com/nanyx-lang/nanyx/issues/1057)). Done in two steps:
   - The scope is carried in types: a scoped context in a type is `TyApply("$Memory", [scope])`, the scope wrapped so
     that `FunctionContexts.ScopedContext` knows it (`FunctionContexts.scoped`). Every scoped context of a name that one
     signature writes shares a scope (`WrittenContexts.read`), and a def's own `$Memory`, where the def is named or
     called, takes the scope its type has there (`WrittenContexts.plain`). The printer leaves the scope out
     (`FunctionContexts.withoutScope`), and so does the backend (`CoreIrLowerTypes.concreteContexts`).
   - `ScopedCalls.fs` checks it. At each call, `enter` finds what the callee supplies from its type: a scoped context a
     parameter's type requires, of a scope the callee's type is generalised over, that its own contexts don't name. The
     scope that is instantiated to is the call's, open while its arguments are checked, and a `memory` block's cells
     take it (`MemoryCells.aroundCall`). Once they are checked, `leave` reports NYX0233 if the scope became one of the
     scopes still open around the call or one a signature writes (the annotated `run`, at what would carry it out), or
     if the call's result mentions it (`tryToFool`, at the use of the cell that brought it in), with the call as a
     related span. The call keeps its type, so nothing around it fails as well. The scope is an ordinary variable
     otherwise: `FunctionContexts.fit` gives a function value's context variable the scoped contexts the block it is
     called in is given, so `{ mk -> memory { mk() } }` is generalised over the scope and supplies `$Memory` in turn.
     Contexts are paired with the same scope first, so a block's requirement of an outer block's `$Memory` goes into
     its context variable.
5. ~~Give a `memory` block its instance, `memory { m -> ... }`~~ ([#1058](https://github.com/nanyx-lang/nanyx/issues/1058)).
   Done: a scoped context's name in a type is the nominal type made from its scope, the one the signature's contexts
   of that name have (`TypeResolution.scopedValueType`), so a value of it mentions the scope and NYX0233 keeps it in
   its block; its constructor makes one of a scope of its own (`TypeResolution.constructedType`). A `use` of a
   `$Memory` instance makes its scope the one a `mut` after it takes (`MemoryCells.afterUse`), and the backend lowers
   it to nothing. Each call's own scope is a variable named `@call` (`FunctionContexts.isCallScope`): `pairUp` never
   pairs two of them, so an inner block's requirement of an outer block's `$Memory` goes into its context variable,
   and the unifier keeps a call's scope as what a plain variable it meets becomes.
