---
title: "Context escape"
description: "Which values may leave the scope that supplies a context, once function types carry contexts"
order: 1
---

This page is a design in progress for what replaces today's two escape checks, NYX0224 for `memory` blocks and
NYX0225 for contexts, once function types carry contexts ([#768](https://github.com/nanyx-lang/nanyx/issues/768)).
The aim is one rule that covers both, with nothing special about `memory`. Nothing here is implemented.

# Two cases

A lambda over a cell must not leave its `memory` block. Today NYX0224 rejects this, and it must stay rejected:

```nanyx
def tryToFool = {
  memory {
    def y = mut 0

    { y := y + 1 }
  }
}
```

A function that only _requires_ a context, and captures nothing that belongs to a scope, can leave freely. Today
NYX0225 can't tell the two apart, and function types can't say `[Foo]`, so this can't be written:

```nanyx
def fooEscapes2: string -> [Foo] () -> string = { s ->
  def inner: [Foo] () -> string = {
    bar(s)
  }

  inner
}
```

The difference is what the lambda depends on. `inner` requires `Foo`, and any caller with a `Foo` can supply one. The
lambda in `tryToFool` depends on a cell, which only its own block can supply.

# What #768 settles, and the hole it opens

With contexts in function types, a lambda requires the contexts it uses where it is called
([Context-qualified types](./context-qualified-types#function-types-and-lambdas)). `inner` has type
`[Foo] () -> string`, the def's signature says so, and `fooEscapes2` checks. The `bar` it calls is the caller's `Foo`,
not the one in scope where `inner` was written. That follows from "require, don't capture", but the language docs
should say it plainly.

#768 alone breaks `memory`, though. The prelude declares `memory: ([$Memory] () -> a) -> a`, and nothing stops `a`
being `[$Memory] () -> int`:

```nanyx
def counter = memory { def y = mut 0; { y := y + 1; y } }   -- [$Memory] () -> int

def a = memory { counter() }   -- 1
def b = memory { counter() }   -- 2: two pure expressions now differ
```

Requiring `$Memory` isn't enough, because every `memory` block supplies one. Something has to stop `counter` leaving
its block.

# Option A: a function's result can't require a context it supplies

**The rule.** A function _supplies_ a context `C` to a parameter when the parameter's type requires `C` and the
function's own type doesn't. Its result must not require `C`. A `use` that loads `C` supplies it to its body in the
same way.

The rule reads off signatures, so no new syntax is needed:

- `memory: ([$Memory] () -> a) -> a` supplies `$Memory` to `block`, so `a` must not require `$Memory`. `counter` is
  rejected where its block ends.
- `fooEscapes2` supplies nothing: its own type doesn't require `Foo`, and it has no parameter that does. It checks.
- `def fooEscapes: [Foo] string -> [Foo] () -> string` requires `Foo` itself rather than supplying it, so it checks
  too. This replaces NYX0225's rule for defs, which rejected it.
- `use (bar = ...) in { bar("x") }` supplies `Foo` and results in a lambda that requires it, so it's rejected, as
  NYX0225 rejects it today. For `Foo` this is stricter than safety needs, because a caller could supply another `Foo`.
  But it's what the code says, and it keeps one rule for all contexts.

**It has to reach type variables.** A check only at `memory`'s call isn't sound:

```nanyx
def run = { mk -> memory { mk() } }   -- ([$Memory] () -> b) -> b

def mk = { def y = mut 0; { y := y + 1; y } }
def counter = run(mk)                 -- [$Memory] () -> int, out of every block
```

Inside `run`, `memory`'s `a` is `run`'s `b`. So `b` must not require `$Memory` either. By the rule, `run` supplies
`$Memory` to `mk`, so `run(mk)` is rejected where `b` would be `[$Memory] () -> int`. Inference therefore needs a
constraint on a type variable: "doesn't require `C`". Effect systems call it a _lacks_ constraint. It is the new piece
of machinery in this option, and it is the main cost. The constraint is derived from the signature's shape, so users
never write it.

**What it costs.**

- A value made in an inner `memory` block can't reach the outer one if it requires `$Memory`, even when it only uses
  the outer block's cells: `$Memory` instances can't be told apart. Make such values in the outer block instead.
- Since instances can't be told apart, naming one buys nothing. The block isn't given its instance, and there is no
  `memory { m -> ... }` form.
- A lambda that makes cells of its own, `memory { { def z = mut 0; z } }`, requires `$Memory`, so it can't leave.
  Put the `memory` inside the lambda.

# Option B: scope parameters

The alternative tells instances apart in their types, as Haskell's `runST` and Flix's regions do. `$Memory(s)` takes a
scope variable `s`. Each call to `memory` gets a fresh, rigid `s`, and its result type must not mention it. Cells and
values like `MArray(s, t)` belong to the innermost instance in scope.

That allows what Option A gives up. A block can be given its instance and name it, and `use` of that instance in an
inner block makes values that belong to the outer one:

```nanyx
memory { m1 ->
  memory {
    use m1 in ...   -- belongs to m1's block
  }
}
```

It needs rank-2 types for scope variables, a way to write a fresh one in `memory`'s signature, and a rule that picks
the innermost instance. It's a larger concept than Option A's constraint.

# Recommendation

Option A. It needs one constraint kind and no new syntax, and its errors can say what went wrong in the user's terms:
"the result of `memory` requires '$Memory'". The extra power of Option B matters only once values like `MArray` exist
and code needs to build one in an inner block for an outer one. Option B can be added then, because it refines
Option A's rule rather than replacing it.

# Settled

- **A lambda with no parameters, checked against a type that takes one parameter, ignores it.** With no expected
  type, `{ ... }` still infers `() -> ...`. Option B needs this so that `memory { ... }` and `memory { m -> ... }`
  check against one signature. It's worth having anyway, but Option A doesn't depend on it.
- **`use` loads a `$Memory`; it can't make one.** `use` loads any context instance you have. `$Memory` is nominal, so
  only the standard library can construct one, and `memory` is the only way to get one.

# Open questions

- **How a rigid type variable meets "doesn't require `C`".** Take `def run2: (() -> b) -> b = { f -> memory { f() } }`.
  `run2` supplies `$Memory` only to its own block, not to `f`, so its signature doesn't say that `b` lacks `$Memory`.
  Anything `f` returns was made outside `run2`'s block, so accepting is safe. But the check inside has to know that
  it's safe, or ask for an annotation.
- **Instances as values.** If a block is ever given its instance, as in Option B, the instance must count as requiring
  its own context. Otherwise `def h = memory { m -> m }` followed by `use h in ...` makes cells outside every block.
- **Whether the rule for `use` should be relaxed for contexts like `Foo`,** where escaping is safe because any caller
  can supply another instance. That needs a way to tell such contexts apart from `$Memory`, which is the special case
  this design is trying to avoid.
- **Desugarings.** Check whether `for` or `__with` ever produce a no-parameter lambda checked against a one-parameter
  type, since the settled lambda rule would change what they accept.
- **Diagnostics.** Name the cell or member that introduced the requirement, and give the supplying `memory` or `use`
  as a related span, as NYX0224 and NYX0225 do today.
