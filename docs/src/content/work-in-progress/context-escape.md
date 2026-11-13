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

A lambda over a cell must not leave its `memory` block. Today NYX0224 rejects this, and it must stay rejected:

```nanyx
def tryToFool = {
  memory {
    def y = mut 0

    { y := y + 1 }
  }
}
```

Requiring `$Memory` isn't enough, because every `memory` block supplies one. The prelude declares
`memory: [c] ([$Memory & c] () -> a) -> a`, and nothing stops `a` being `[$Memory] () -> int`:

```nanyx
def counter = memory { def y = mut 0; { y := y + 1; y } }   -- [$Memory] () -> int

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
    def z = mut 0   -- belongs to m1's block
    ...
  }
}
```

`memory`'s block then takes the instance, `memory: [c] ([$Memory & c] $Memory -> a) -> a`, and `memory { ... }` still
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

# Open questions

- **Whether a record holding a lambda takes on its contexts**
  ([#1059](https://github.com/nanyx-lang/nanyx/issues/1059)). `(f = { m: string -> println(m) })` would be
  `[Console] (f: string -> ())`, and `Log(log = { m -> println(m) })` would be `[Console] Log`, which `use` would
  require. That is the general design in [Context-qualified types](./context-qualified-types), and it would let an
  instance built as a value discharge a context, as one written at a `use` can.
- **Diagnostics.** The escape error should name the cell or member that introduced the requirement, and give the
  supplying `memory` call as a related span, as NYX0224 does today.

# Plan

1. ~~Let a `use` give a member a lambda that needs more contexts, required where the `use` is written~~
   ([#1054](https://github.com/nanyx-lang/nanyx/issues/1054)). Done: see `UseDischarge.fs` above.
2. ~~Make a lambda that uses a cell require `$Memory`~~ ([#1055](https://github.com/nanyx-lang/nanyx/issues/1055)).
   Done: a read or change of a cell requires `$Memory` of the lambda around it (`MemoryCells.recordUse`), so
   `{ word -> count := count + 1 }` is `[$Memory] string -> ()`, and a lambda that calls it requires `$Memory` too.
   NYX0224 still keeps such a lambda in its block.
3. ~~Parse and format `scoped context`, which must be nominal~~ ([#1056](https://github.com/nanyx-lang/nanyx/issues/1056)).
   Done: the parser records `Scoped` (with the keyword's span) among the definition's modifiers,
   `TypeDefInfo.IsScoped` carries it, a scoped context that isn't nominal is NYX0232, and `$Memory` is declared
   `scoped`. Nothing checks escape by it yet.
4. Keep what requires a scoped context inside the call that supplies it, replacing NYX0224
   ([#1057](https://github.com/nanyx-lang/nanyx/issues/1057), after 2 and 3).
5. Give a `memory` block its instance, `memory { m -> ... }` ([#1058](https://github.com/nanyx-lang/nanyx/issues/1058),
   after 4).
