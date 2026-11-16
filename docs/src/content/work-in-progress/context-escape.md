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

So there is no distinction between "static" contexts such as `Console` and "instance" ones such as `Memory` in the
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
- **Only an instance written at the `use` itself.** An instance built as a value would have nowhere to say what it
  needs, and since [contexts are not types](#contexts-are-not-types), `def inst = Log(log = { m -> println(m) })` is
  NYX0248.
- **A reusable handler is a function,** as it is in Koka:

  ```nanyx
  def withConsoleLog: [Console, c] ([Log, c] () -> a) -> a = { body ->
    use Log(log = { m -> println(m) })
    body()
  }

  @entry
  def main = {
    withConsoleLog {
      greet("world")
      log("done")
    }
  }
  ```

  The lambda passed to it is checked against `[Log, c] () -> a`, so its body has `Log`: it can call `log` and what
  requires `Log`, and its type requires `Log`, which `withConsoleLog` supplies. Contexts it uses besides bind `c`, so
  `main` requires `Console`; where the parameter has no context variable they are NYX0227. A `[Log] () -> ()` with
  `withLog(body)` works the same. `Contexts.extendEnvForLambdaArgument` binds the members of the plain contexts the
  parameter's type requires, at a call whose callee's type is known
  ([#1218](https://github.com/nanyx-lang/nanyx/issues/1218)). `RunLambdaSuppliedContextTests.fs` runs this example.
  A context a function type carries is named as it is declared: a `module db =` block's `Tx`, which the module that
  imports the block calls `Tx` too, is `db.Tx` in both function types (`WrittenContexts.canonical`), so the lambda's
  requirement meets the parameter's ([#1486](https://github.com/nanyx-lang/nanyx/issues/1486)).

- **The backend needs nothing new.** `CoreIrLowerMembers.coerce` already adapts a function value given where one that
  passes fewer members is expected, capturing the members it needs where it is given. A `use` binding goes through it.

`UseDischarge.fs` does this in the type checker ([#1054](https://github.com/nanyx-lang/nanyx/issues/1054)). A `use`
marks the instance it loads; the constructor call checks each lambda it is given against its member's type without
the contexts the lambda needs besides, and records those as used where the `use` is written, so a lambda around the
`use` requires them too. The lambda's parameters take their types from the member wherever the constructor is called.
A function given by its name, `use Move(move = bobChooses)`, is taken the same way when an instance of each context it
needs besides is in scope at the `use`; otherwise it keeps its type, and is `NYX0227` as before
([#1265](https://github.com/nanyx-lang/nanyx/issues/1265)).

A `use` of a record written out, `use (log = { m: string -> println(m) })`, used to check and run, because its
members had no declared types to fit. A `use` now loads only a declared context
([#1448](https://github.com/nanyx-lang/nanyx/issues/1448)).

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

Requiring `Memory` isn't enough, because every `memory` block supplies one. The prelude declares
`memory: [c] ([Memory, c] () -> a) -> a`, and nothing stops `a` being `[Memory] () -> int`:

```nanyx
def counter = memory { mut y = 0; { y := y + 1; y } }   -- [Memory] () -> int

def a = memory { counter() }   -- 1
def b = memory { counter() }   -- 2: two pure expressions now differ
```

## Scoped contexts

A context declared `scoped` belongs to the scope that supplies it, and nothing that requires it can leave that scope:

```nanyx
scoped export context Memory = protected ()
```

- **Where a scope starts.** A function _supplies_ a context to a parameter when the parameter's type requires it and
  the function's own type doesn't: `memory` supplies `Memory` to its block. For a scoped context, each call to a
  function that supplies it starts a fresh, rigid scope. Every requirement of the context inside belongs to that
  scope, and the call's result type must not mention it. This is Haskell's `runST`, Koka's `local<h>` and Flix's
  regions, with the scope variable implied by the marker rather than written, so users never see it.
- **A value can belong to a scope.** A type declared `type Cursor = [Tx] protected (...)` carries the scope of the `Tx`
  each value was made under, so a value of it, or anything holding one, can't leave the call that supplies that `Tx`
  either ([Types in a scope](../advanced/contexts#types-in-a-scope),
  [#1521](https://github.com/nanyx-lang/nanyx/issues/1521)).
- **Only scoped contexts are restricted.** Every other context keeps "require, don't capture", so
  `withConsoleLog { { log("x") } }` above may return a lambda that requires `Log`.
- **Instances come only from their supplier.** A scoped context must be either `protected` or `private`, so only its own module can
  construct an instance, and its supplying function is where each instance and scope starts. `use` only loads an
  instance it is given. `scoped context Tx = (...)` is an error. Any module may declare a scoped context, such as a
  `Transaction` in a database library.
- **A type variable bound outside the scope is safe.** `def run2: (() -> b) -> b = { f -> memory { f() } }` checks:
  `b` can't mention a scope made inside `run2`.
- **It reaches type variables.** `def run = { mk -> memory { mk() } }` passes `mk` the block's `Memory`, so
  `run(mk)` is rejected where its result would require that block's `Memory`.
- **It fits discharge at a `use`.** A `use` in a block whose member's lambda changes a cell requires `Memory` where
  the `use` is written, which is inside the block.

A block can name its `Memory`, `memory { context m -> ... }` (see
[Contexts are not types](#contexts-are-not-types)). The name can only be loaded, so `memory { context m -> m }` is
NYX0245. `mut` uses the innermost `Memory` in scope, and `use m1` in an inner block makes `m1` the innermost, so the
inner block can make cells that belong to the outer one:

```nanyx fragment
memory { context m1 ->
  memory {
    use m1
    mut z = 0   -- belongs to m1's block
    ...
  }
}
```

`memory`'s block takes nothing, `memory: [c] ([Memory, c] () -> a) -> a`.

## Options considered

- **A lacks constraint for every context** (Option A in earlier drafts): a function's result must not require a
  context it supplies, for any context. It needs no marker, but it also rejects a handler function returning a lambda
  that requires the context it handles, which "require, don't capture" allows. It can't tell instances apart either,
  so a value made in an inner block could never reach an outer one.
- **An explicit scope variable**, `context Memory(s)` with `forall s` in `memory`'s signature. It's what `scoped`
  means, but users would see `s` in hovers and errors, and it needs rank-2 syntax.
- **Capturing "static" contexts** such as `Console` into a lambda whose type drops them. A pure-looking function could
  then print.
- **Capture sets** in function types, as Effekt and Scala's capture checking have: `string -> () ^{Console}`. More
  expressive, but a second context list on every function type.

# Settled

- **A lambda with no parameters, checked against a type that takes one parameter, ignores it.** With no expected
  type, `{ ... }` still infers `() -> ...`, and `{ () -> ... }` always takes unit. Done in
  [#813](https://github.com/nanyx-lang/nanyx/issues/813): the syntax tree marks a lambda written `{ () -> ... }`
  (`takesUnit` on `Lambda`), and before the backend reads a module, `ExpressionRewrites.nameIgnoredParameters` gives
  each `{ ... }` whose type takes an input a parameter it ignores. None of the parser's desugarings (`for`, `__with`,
  `__rec`, workflow blocks) makes a lambda without parameters that is checked against a one-parameter type, so they
  accept what they did.
- **`use` loads a `Memory`; it can't make one.** `use` loads any context instance you have. `Memory` is nominal, so
  only the standard library can construct one, and `memory` is the only way to get one.

# Workflows: a context and a function

Done ([#1305](https://github.com/nanyx-lang/nanyx/issues/1305)), decided apart from the design below, which it doesn't
need: a builder's `use` is of a constructor written in place. A workflow builder is a function that runs its block
under a declared context, whose members are the builder's keywords. `proc` in `prelude-results.nyx` is the context
`Proc` and the function `proc`:

```nanyx fragment
export context Proc = (try: ..., return: ..., zero: ..., for: ...)

export def proc: [c] ([Proc, c] () -> #some(b) | r) -> #some(b) | r = { block ->
  use Proc(
    try = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    return = { value -> #some(value) }
    zero = #some()
    for = { items, body, rest -> procFor(items, body, rest) }
  ) in block()
}
```

- **The desugaring is a call.** `proc { ... }` is `proc({ ... })`, and `retrying(3) { ... }` is `retrying(3, { ... })`:
  a call whose last argument is a lambda without parameters is a builder's block (`WorkflowDesugaring.BuilderBlock`).
  Inside the block, `try x`, `return x`, `zero`, `for` and custom keywords (`await! x`) are calls of the members of
  those names, with continuations, as before. The block's lambda requires the context, so the members are in scope
  in it by name (`Contexts.extendEnvForLambdaArgument`), and the builder's `use` supplies it. A keyword the context
  in scope has no member for is reported at the keyword (`WorkflowMembers`), as a missing `zero` or `for` was.
- **The builder writes its type.** The block's type in it, `[Proc, c] () -> ...`, is what gives the block the
  members: the checker can't infer that `f` in `{ f -> use Proc(...) in f() }` requires `Proc`. A member that takes a
  continuation takes one with a context variable (`[c] a -> ...`), since the continuation calls members too.
- **`apply` and `@workflow` are gone.** The builder function is what `apply` did, and what `@workflow` would have
  generated. `@customKeywordType` goes on the member in the `context` declaration
  ([#1317](https://github.com/nanyx-lang/nanyx/issues/1317)): the parser reads it there as a `TypeFieldAttribute`,
  and `Attributes.fieldAttributeErrors` accepts only `#bind`, the one kind the desugaring implements.
- **A block without a keyword is an ordinary call.** `proc { 1 + 2 }` used to be its block, `3`. Now it is
  `proc({ 1 + 2 })`, a type error, and `proc { #some(3) }` is `#some(3)`.
- **Members are generic at each call** ([#1304](https://github.com/nanyx-lang/nanyx/issues/1304)): a member's own type
  variables, those the context's parameters don't give it, are instantiated afresh at each call, in a function that
  requires the context, in a block given it, and after a `use` of a plain one (`Contexts.extendEnvWithContextsFrom`,
  `extendEnvWithRecordFields`). The context's own parameters (`a` in `Eq(a)`) stay fixed by the instance. Reading a
  member as a value, as a block reads `zero`, requires its context of the lambda around it, as a call of it does.
- **The backend boxes them** ([#1160](https://github.com/nanyx-lang/nanyx/issues/1160)): a member of a context without
  parameters has the values of its own type variables boxed (`IrBox`, a closure whose function returns the value), in
  its context's layout and in the lambda a `use` binds to it, which is found by the context the `use` names
  (`LowerInput.UseBindingContexts`), since two contexts may have members of the same name. A call converts what it
  passes and is given back, rebuilding a tag's cell, a record or a list around the parts it converts, and adapts a
  continuation to do the same (`CoreIrLowerBoxes`, `CoreIrLowerMembers.coerce`). A member of a parameterised context
  generic in variables of its own is not boxed yet, and neither is a `use` of an instance built as a value rather than
  written at the `use`.
- **An imported context keeps its own variables.** A module's substitution leaves alone every type variable of a type
  definition, not only its parameters (`Library.resolveTypes`): an imported definition's variables are numbered by its
  own module, and the importer's constraints bound `Proc`'s `b`, `c` and `y` once they were shared ids.

## What it costs

There is no instance at run time. The backend passes a context's members one by one, as closures, to each function
that requires it ([Context calls](./context-calls)), and a `use` binds each member's lambda to a local. `proc` no
longer rebuilds a record at each keyword, and a generic top-level value is now built once for each type it is read at
([#1151](https://github.com/nanyx-lang/nanyx/issues/1151)). But each keyword's call now boxes what it passes and
unboxes what it is given back, until inlining ([#745](https://github.com/nanyx-lang/nanyx/issues/745)) removes the boxes
where every call is known.

Measured by the allocation counter of a `--check-memory` build over 100 blocks, and the time of 100,000 calls from the
host (the call from .NET included), for a `proc` block of three `try`s and a `return` on `int` payloads: 15
allocations and about 544 ns per block with `main` before the change, and 29 allocations and about 724 ns after it.
Each `try` boxes its payload into a new `#some` cell, adapts its continuation (which unboxes the payload it is given
and boxes the result it returns into a new cell), and unboxes its result into a new cell again; `return` boxes its
value. A block of three `bind`s and a `ret` written by hand on a `Proc` context makes 28 with the members generic,
against 11 with the same members written on `int`. Inlining and simplifying after it
([#1339](https://github.com/nanyx-lang/nanyx/issues/1339)) remove the closures and adapters, which leaves the boxes: 17
allocations a block. Copying a match into the branches whose tag it tests
([#1340](https://github.com/nanyx-lang/nanyx/issues/1340)) removes the boxes too: 1 allocation, the `#some` the block
returns, and about 9 ns a block against 240 ns without inlining. With aliases copied away and locals read at once kept
on the stack ([#1341](https://github.com/nanyx-lang/nanyx/issues/1341)), the block compiles to the same WebAssembly as
the nested match written by hand, and in the benchmark's loop it allocates nothing: about 3 ns a block (see
[Inlining](./inlining#what-it-costs)).

### Measured before the change

A `proc` block with three `try`s and a `return`, on `int` payloads, was compiled with `main` at dcda5b3b and run 20
million times in a loop. There are four variants: (A) `proc { ... }`; (B) what A desugars to, written out (`def t =
proc.try` then `t(x, { v -> ... })`); (B2) the documented `use proc in` form, with the members renamed `bind` and
`ret`, since `try` and `return` can't be called by name; (C) the proposal: `context Proc`, a `procH` that runs `f`
under `use Proc(...) in f()`, and the members called by hand. Debug and `--release` emit the same WAT. Allocations
and `call_indirect`s are counted from the WAT, along the path where every `try` succeeds. The time is the median of
five runs, minus a one-iteration run for compiling and start-up, and runs differed by up to 30%.

|                              |      A |      B |     B2 |      C |
| ---------------------------- | -----: | -----: | -----: | -----: |
| WAT of the block's functions | 6.2 KB | 6.0 KB | 6.0 KB | 6.5 KB |
| Allocations per block        |     19 |     19 |      9 |      9 |
| `call_indirect` per `try`    |      2 |      2 |      2 |      3 |
| `call_indirect` per block    |      7 |      7 |      7 |     11 |
| Time per block               | 192 ns | 204 ns | 141 ns | 132 ns |

- **The old `proc` rebuilt its record at every keyword.** `proc` was generic, so a read of it is a call that makes
  the whole record: three closures and the record, four allocations, for each of the four keywords. That is 16 of A's
  19 allocations, and why C, which makes its two member closures once per block, is faster than A today
  ([#1151](https://github.com/nanyx-lang/nanyx/issues/1151)).
- **C makes two closures per `try`.** The continuation requires `Proc`, and `bind`'s parameter is `[c] a -> ...`, so
  the backend wraps it in an adapter closure (`__adapt_...`), and calling it makes a third `call_indirect`. C's other
  allocations are the block lambda and the two member closures at `procH`'s `use`.
- **With [#743](https://github.com/nanyx-lang/nanyx/issues/743)**, `bind` and `ret` at `procH`'s `use` capture nothing and become static: C drops to 7 allocations per
  block (the block lambda, three continuations and three adapters) and keeps 11 `call_indirect`s. Measured once #743
  was done, by the allocation counter of a `--check-memory` build over 100 blocks: C's blocks allocate 2 fewer each
  (13 to 11, counting the `#some` cells the table leaves out), so 9 to 7, as predicted. [#744](https://github.com/nanyx-lang/nanyx/issues/744) doesn't help
  C: the members reach the block as closure parameters through `f`.
- **With [#745](https://github.com/nanyx-lang/nanyx/issues/745) as well**, inlining `procH` into its caller exposes `f` as the block lambda and the members as top-level
  functions. The block lambda isn't made, `f()`, `bind` and `ret` become direct calls, and a continuation no longer
  needs an adapter, since the members it reads are known. That leaves three continuations and the three calls of them
  in `bind`: 3 allocations and 3 `call_indirect`s per block, fewer than A makes today.
# Contexts are not types

Decided by the maintainer in [#1141](https://github.com/nanyx-lang/nanyx/issues/1141). The work is in the issues under
[What changes](#what-changes).

A context instance can be built only where a `use` loads it, so it is never a value. The rule above (only an
instance written at the `use` discharges) meant a handler couldn't be moved into a function of its own, and now a
function can't return an instance at all:

```nanyx fragment
def consoleLogger: [Console] () -> Log = {   -- NYX0247: 'Log' is a context, not a type
  Log(log = { m -> println(m) })              -- NYX0248: only a `use` writes an instance
}

def main = {
  use consoleLogger()   -- NYX0249: a `use` loads only a constructor or a `context` name
  log("hello")
}
```

The design draws the line there, and makes every helper a function that takes the code to run:

- **A context is not a type.** A context's name may appear only in a context list (`[Log]`) and as the constructor in
  a `use`. It can't be a parameter's, a result's or a field's type, so `() -> Log` is an error.
- **No def holds an instance.** `def inst = Log(...)` is an error, and so is a function that returns one. A constructor
  call is valid only as the instance of a `use`.
- **`use` loads only a declared context.** Its instance is always a context's constructor, `use Log(log = ...)`. A plain
  record, `use (log = ...)`, can no longer be loaded, so loose members are never in scope. Contexts are their own kind
  of thing, a system parallel to types, but they behave as types do: a plain one matches by members, and a `protected`
  or `private` one only itself (see [Decided along the way](#decided-along-the-way)). `use` and a context list both name a declared context.
- **A reusable handler is a function that runs a lambda under a `use`.** This is the form the section above calls "a
  reusable handler", and the only one:

  ```nanyx fragment
  def withDefaultLogger = { prefix, f ->
    use Log(log = { msg -> println("[{prefix}] {msg}") }) in f()
  }
  ```

  It takes parameters as any function does, and its members' extra contexts are discharged at its `use`, so
  `withDefaultLogger` requires `Console`.
- **A lambda can name a context it is given, and can only `use` it.** A lambda may write `context x` before its
  parameters, as in `memory { context m -> ... }`. `x` names the instance of a context the lambda's type requires,
  the one whoever calls the lambda supplies. The only thing `x` can do is be loaded: `use x` or `use x in ...`. It
  can't be passed, returned, stored, compared or written in a type, so the instance still never becomes a value.

  ```nanyx fragment
  memory { context outer ->
    mut total = 0
    memory {
      use outer
      mut z = 0          -- belongs to the outer block, as total does
      ...
    }
  }
  ```

  - **It isn't a parameter.** The lambda's arity and type don't change: `memory`'s block is `[Memory, c] () -> a`,
    not `Memory -> a`, and a caller passes nothing for it.
  - **Which context.** When the lambda's type requires one context, `context x` names it. When it requires several,
    the name says which, `context m: Memory`, and a lambda can name more than one (`{ context m: Memory, context l:
    Log -> ... }`).
  - **Inner lambdas.** An inner lambda may capture the name, but only to `use` it. For a scoped context such as
    `Memory`, the scope check ([#1057](https://github.com/nanyx-lang/nanyx/issues/1057)) keeps the name inside its
    block, as it keeps everything that requires the context.
  - **Any lambda.** It's general: a def's lambda can name its own required context, as in `def run: [Log] () -> () = {
    context l -> ... use l in other() }`, which loads the same `Log` again, for instance after an inner `use`
    shadowed it.

## What changes

- ~~The prelude's builders (`proc` and any others) become a `context` and a function. The cookbook's `OptionChain` and
  the `@workflow` and `@customKeywordType` docs change with them, and `[typeof optionChain]` goes.~~ Done, see
  [Workflows](#workflows-a-context-and-a-function).
- ~~`context x` in a lambda, usable only by `use` ([#1445](https://github.com/nanyx-lang/nanyx/issues/1445)).~~
  Done. The parser puts the names on the lambda's body (`ContextNames`), and `ContextNames.bind` binds each, with the
  lambda's parameters, to the plain context it names among those the lambda is given: each context whose members
  are all bound in the body as its own by a signature (the def's, or the type the lambda is checked against), and
  `Memory` in a `memory` block. Any other context without members, or one with parameters, can't be named yet. Each
  name keeps its context's
  members as the body has them, and `use l` binds them again (NYX0245 for any other use, NYX0246 for a name that
  matches no context or several). Before the backend, `ExpressionRewrites.writeContextNames` keeps each member in a
  local at the top of the body and writes `use l` as a `use` of a record of those locals, which the backend
  compiles already, inner lambdas that capture them included.
- ~~`memory { m -> ... }` ([#1058](https://github.com/nanyx-lang/nanyx/issues/1058)) becomes
  `memory { context m -> ... }`, and `memory`'s signature becomes `[c] ([Memory, c] () -> a) -> a`~~
  ([#1446](https://github.com/nanyx-lang/nanyx/issues/1446)). Done: `ContextNames.bind` binds a name to the `Memory`
  of the `memory` block it is in (`MemoryCells.blockScope`), keeping the block's scope, and `use m` makes that scope
  the one a `mut` takes (`MemoryCells.withBlockScope`). A `use` of a name for a context without members loads
  nothing, so `ExpressionRewrites.writeContextNames` drops it before the backend. `Memory` is never a value: its
  name in a type is the plain nominal type until [#1447](https://github.com/nanyx-lang/nanyx/issues/1447) makes it an
  error.
- ~~A context's name in a type is an error, and so is a constructor call that isn't a `use`'s instance~~
  ([#1447](https://github.com/nanyx-lang/nanyx/issues/1447)). Done: `TypeResolution.unknownTypeErrors` reports a
  context's name anywhere in an annotation or a type definition outside a context list (NYX0247), except the context
  `use C(T)(members)` is desugared with (`ensureInstanceContextExists`), and a constructor call `UseDischarge.mark`
  didn't mark as a `use`'s instance is NYX0248. `examples/Nim.nyx` already gave its strategies as plain functions.
  The monoid instances in `examples/traitsAndTypeclasses.nyx` are written at their `use`s rather than as handlers,
  since a lambda passed to a function isn't given a parameterised context its type requires yet
  (`Contexts.extendEnvForLambdaArgument`), and `examples/sql-utils.nyx`'s data source opens a `Session` record that
  `withConnection` loads as the `Connection`.
- ~~A `use` loads only a context's constructor or a `context x` name~~
  ([#1448](https://github.com/nanyx-lang/nanyx/issues/1448)). Done: `UseDischarge.notLoadable` makes NYX0249 of a
  `use` of anything else, a record, a def's name, a call or a field, before its value is checked; a `context x`
  name is loaded before that is asked (`ContextNames.loaded`). So [#1137](https://github.com/nanyx-lang/nanyx/issues/1137)
  (the backend can't compile `use f()` or `use x`) is no longer needed, and is closed. The tour's collection builders
  and the Context-qualified types and Context calls pages write a context's constructor. The backend still compiles a
  `use` of a record, which is what `ExpressionRewrites.writeContextNames` makes of `use l`.
- [#1059](https://github.com/nanyx-lang/nanyx/issues/1059) is settled: context instances are never values, and a
  record holding a lambda doesn't take on its contexts ([Context-qualified types](./context-qualified-types)).
- ~~A protected or private context matches only itself ([#1443](https://github.com/nanyx-lang/nanyx/issues/1443)),
  and a private one's members can be called only in its home module
  ([#1449](https://github.com/nanyx-lang/nanyx/issues/1449)).~~ Done, see
  [Protected and private contexts](#protected-and-private-contexts).

## Decided along the way

- **Whether one context can serve for another.** Decided by the maintainer: contexts match as types do, though they
  are a system parallel to types, not types.
  - **A plain context matches by members.** A function that requires `Add(a)` is given the `Num(a)` in scope, because
    a `Num` has every member an `Add` has. This keeps the Contexts page's `mean` and the
    [Operator contexts](./operator-contexts) rule that an operator reads the smallest context with its member, so
    given `def f = { + }`, a `g: [Num(a)]` can call `f`. No relation needs declaring.
  - **A `protected` or `private` context matches only itself.** Otherwise any context with the same members could be
    loaded in its place, and the guarantee those words give (only the home module makes an instance) would be
    forgeable: `use Fake(commit = ...)` would satisfy a function that requires a protected `Tx`. Done in
    [#1443](https://github.com/nanyx-lang/nanyx/issues/1443): `Contexts.ensureContextAvailable` requires each member in
    scope of a protected or private context without parameters to have the context's own symbol, else NYX0205.
    A protected or private context without members has no member to find, so the environment binds a name no code
    can write wherever an instance of it is supplied: by a signature, by a function that supplies it to a lambda, or
    by a `use` (`Contexts.withSupplied`). A call that requires it where that name is not bound is NYX0205
    ([#1479](https://github.com/nanyx-lang/nanyx/issues/1479)). `HostRuntime` is left to `Hosts`, and a scoped
    context to `ScopedCalls`.
  - **A protected context may serve for a plain one** whose members it has, as a protected type may be used as its
    representation: that hands out nothing the plain context didn't already allow.
  - Declaring the relation (`Num` includes `Add`) and having no relation at all were considered. Declaring it would
    need every relation written out, including for users' own contexts, and having none would make `mean` require
    `Add` and `Div` separately.

## Protected and private contexts

Done in [#1449](https://github.com/nanyx-lang/nanyx/issues/1449). On a context, `protected` and `private` are about
access. Any module can name either in a context list and call what requires it, but only the home module can write its
constructor in a `use`: NYX0212, the check every nominal type's constructor has, which says "context" for one. Other
modules can call a `protected` context's members. Only the home module can call a `private` one's, or read them as
values (NYX0244, `Contexts.privateContextOutsideHome`, checked by `Contexts.checkContextUse` wherever a name is read),
so it's a capability token: `[Tx]` can be required everywhere, but only `Tx`'s module uses what it holds. That differs
from leaving out `export`, which stops other modules naming it at all. A scoped context must be one or the other
(NYX0232), since a scope starts where its supplier makes the instance.

Hover hides a private context's members outside its file, as it hides a private type's shape. Within one file, a
`module name =` block's private type or context still shows its shape everywhere in the file.

# Plan

1. ~~Let a `use` give a member a lambda that needs more contexts, required where the `use` is written~~
   ([#1054](https://github.com/nanyx-lang/nanyx/issues/1054)). Done: see `UseDischarge.fs` above.
2. ~~Make a lambda that uses a cell require `Memory`~~ ([#1055](https://github.com/nanyx-lang/nanyx/issues/1055)).
   Done: a read or change of a cell requires `Memory` of the lambda around it (`MemoryCells.recordUse`), so
   `{ word -> count := count + 1 }` is `[Memory] string -> ()`, and a lambda that calls it requires `Memory` too.
3. ~~Parse and format `scoped context`, which must be protected or private~~ ([#1056](https://github.com/nanyx-lang/nanyx/issues/1056)).
   Done: the parser records `Scoped` (with the keyword's span) among the definition's modifiers,
   `TypeDefInfo.IsScoped` carries it, a scoped context that is neither is NYX0232, and `Memory` is declared
   `scoped`.
4. ~~Keep what requires a scoped context inside the call that supplies it, replacing NYX0224~~
   ([#1057](https://github.com/nanyx-lang/nanyx/issues/1057)). Done in two steps:
   - The scope is carried in types: a scoped context in a type is `TyApply("Memory", [scope])`, the scope wrapped so
     that `FunctionContexts.ScopedContext` knows it (`FunctionContexts.scoped`). Every scoped context of a name that one
     signature writes shares a scope (`WrittenContexts.read`), and a def's own `Memory`, where the def is named or
     called, takes the scope its type has there (`WrittenContexts.plain`). The printer leaves the scope out
     (`FunctionContexts.withoutScope`), and so does the backend (`CoreIrLowerTypes.concreteContexts`). Where one type
     requires a scoped context at more than one scope, the printer numbers them in order of first appearance,
     `([Memory₁] () -> a), ([Memory₂] () -> b) -> b` (`Unifier.printingFor`), in hover, inlay hints, error messages
     and `nanyx check`; at one scope it is plain `[Memory]`
     ([#1553](https://github.com/nanyx-lang/nanyx/issues/1553)). No source can write the numbered form yet
     ([#1591](https://github.com/nanyx-lang/nanyx/issues/1591)), so Write the inferred type refuses such a type.
   - `ScopedCalls.fs` checks it. At each call, `enter` finds what the callee supplies from its type: a scoped context a
     parameter's type requires, of a scope the callee's type is generalised over, that its own contexts don't name. The
     scope that is instantiated to is the call's, open while its arguments are checked, and a `memory` block's cells
     take it (`MemoryCells.aroundCall`). Once they are checked, `leave` reports NYX0233 if the scope became one of the
     scopes still open around the call or one a signature writes (the annotated `run`, at what would carry it out), or
     if the call's result mentions it (`tryToFool`, at the use of the cell that brought it in), with the call as a
     related span. The call keeps its type, so nothing around it fails as well. The scope is an ordinary variable
     otherwise: `FunctionContexts.fit` gives a function value's context variable the scoped contexts the block it is
     called in is given, so `{ mk -> memory { mk() } }` is generalised over the scope and supplies `Memory` in turn.
     Contexts are paired with the same scope first, so a block's requirement of an outer block's `Memory` goes into
     its context variable. Any other scoped context of a name already paired, unless it is of another call's scope, is
     paired with the same one: each call in a block that requires `Tx` requires `Tx` of a scope of its own, and all of
     them are the block's, so `transaction { step(step(1)) }` requires it once
     ([#1553](https://github.com/nanyx-lang/nanyx/issues/1553)).
   - A scoped context has no members, so the member check at a call never finds it missing. `ScopedCalls.unsupplied`
     checks it instead, from `Contexts.ensureContextAvailable`: inside a lambda of the def around the call
     (`LambdaContexts.withinLambda`) the lambda requires it, as before. Outside one, the def must write it, supply it to
     a function it is given (as `memory` does), or take a cell, for `Memory`; otherwise the call is NYX0211, and in a
     top-level value or a script's statements it is an error too. A top-level def that writes no type is inferred to
     require each scoped context its body requires outside a lambda, all of one scope
     (`InferState.InferredScopedContexts`), and its type carries them, so its callers are checked in turn. A `for`
     loop's body runs where the loop is written, so the loop is checked the same way
     ([#1561](https://github.com/nanyx-lang/nanyx/issues/1561)).
5. ~~Let a `memory` block name its `Memory`, `memory { context m -> ... }`~~
   ([#1058](https://github.com/nanyx-lang/nanyx/issues/1058), then
   [#1446](https://github.com/nanyx-lang/nanyx/issues/1446) for the `context m` form). Done: see
   [What changes](#what-changes). Each call's own scope is a variable named `@call` (`FunctionContexts.isCallScope`): `pairUp` never
   pairs two of them, so an inner block's requirement of an outer block's `Memory` goes into its context variable,
   and the unifier keeps a call's scope as what a plain variable it meets becomes.
