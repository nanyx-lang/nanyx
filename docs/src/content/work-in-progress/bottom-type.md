---
title: "The bottom type"
description: "`undefined`, the type of an expression that never gives a value, and what is built on it"
order: 23
---

> **Status: in progress.** The type checker has `undefined`, and annotations can name it. The prelude's `panic`
> returns it, and the official linter's `unreachableCode` rule reports the statements after one of its type
> ([#498](https://github.com/nanyx-lang/nanyx/issues/498)). Open questions are [below](#open-questions).

## What it is

`undefined` is the type of an expression that never gives a value: a call to a function that stops the program, or
that never ends. No value has this type, so wherever a value of any type is expected, such an expression fits:

```nanyx
def stop: int -> undefined = { x -> stop(x) }

def double = { n -> if n > 0 then n * 2 else stop(n) }   -- int -> int
```

## How it unifies

`undefined` is the union of no tags, `TyUnion []` (`Unifier.bottomType`): the same type a closed union's rest is
solved to when it adds no tags.

- **It is assignable to every type, one way.** Under an `Assignable` constraint, `undefined` given where any type is
  expected is satisfied: a call's argument, a lambda's body against the result its annotation declares, a def's value
  against its annotation. Nothing else is assignable to `undefined`, so a parameter of type `undefined` takes only a
  value of that type. Two closed unions agree when one's tags are among the other's, so a closed union can be given
  for `undefined` too, as it could before.
- **`Equal` is unchanged.** An `Equal` constraint treats it as the empty union it is, so it does not agree with an
  `int`. The items of a list literal are made equal to the first, so `[1, stop(0)]` is an error.
- **It binds no variable.** Given where a variable is expected, it is dropped, and the variable takes what the other
  values given for it are. The order constraints are solved in (newest first) then makes no difference.
- **Branches leave it out.** `joinBranchTypes` (`BranchJoin.fs`) drops the branches of an `if` or `match` whose type
  is `undefined`, and joins the rest. The whole is `undefined` only when every branch is.

## How it is shown

As `undefined`, the name an annotation writes, in hovers, signatures and messages.

## What is built on it

- **`panic`**, in the prelude: `string -> undefined`. It writes `panic: ` and its message on a line to standard
  error, then calls the intrinsic `__nanyx_exit(1)`, `int -> undefined`, which the backend lowers to WASI's
  `proc_exit` followed by `unreachable`. Panicking is not an effect, so `panic` needs no context.
- **The `unreachableCode` rule** (`Linters.fs`) looks at every block of the module. The first statement whose type
  is `undefined` (an expression, or the value a def binds) that is not the block's last makes the rest of the block
  unreachable, and they are reported together, with a fix that removes them when the statement before them is not a
  def.

## Open questions

- **Arguments after one that never returns.** In `f(panic("a"), g())`, `g()` never runs. The rule only looks at the
  statements of blocks.
- **Inferred divergence.** A function that never returns but is not annotated, such as `def loop = { x -> loop(x) }`,
  is inferred as `a -> b`, not `a -> undefined`, so calls to it are not seen as diverging.
- **List literals and other `Equal` constraints.** `[1, panic("x")]` is an error, since a list's items are made equal.
  Whether those constraints should let `undefined` through too is open.
