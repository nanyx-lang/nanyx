---
title: "The bottom type"
description: "`undefined`, the type of an expression that never gives a value, and what is built on it"
order: 23
---

> **Status: in progress.** The type checker has `undefined`, and annotations can name it. Still to come: a
> `panic` in the prelude that stops the program, and a linter rule for code after an expression that never gives a
> value ([#498](https://github.com/nanyx-lang/nanyx/issues/498)).

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
