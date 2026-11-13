---
title: "The bottom type"
description: "How the type checker handles `undefined`, the type of an expression that never gives a value, and what is still open"
order: 23
---

`undefined` is built. The language docs describe it in [Top and bottom types](../tour/types#top-and-bottom-types),
`panic` in [Error handling](../tour/error-handling), and the `unreachableCode` rule in the
[official linter](../standard-tools/official-linter#unreachable-code-path). This page keeps how the type checker handles it and what is
still open.

## How it unifies

`undefined` is the union of no tags, `TyUnion []` (`Unifier.bottomType`): the same type a closed union's rest is
solved to when it adds no tags.

- **It is assignable to every type, one way.** Under an `Assignable` constraint, `undefined` given where any type is
  expected is satisfied: a call's argument, a lambda's body against the result its annotation declares, a def's value
  against its annotation. Nothing else is assignable to `undefined`, so a parameter of type `undefined` takes only a
  value of that type. Two closed unions agree when one's tags are among the other's, so a closed union can be given
  for `undefined` too, as it could before.
- **`Equal` is unchanged.** An `Equal` constraint treats it as the empty union it is, so it does not agree with an
  `int`.
- **It binds no variable.** Given where a variable is expected, it is dropped, and the variable takes what the other
  values given for it are. The order constraints are solved in (newest first) then makes no difference.
- **Branches leave it out.** `joinBranchTypes` (`BranchJoin.fs`) drops the branches of an `if` or `match` whose type
  is `undefined`, and joins the rest. The whole is `undefined` only when every branch is. The items of a list literal
  are joined the same way, so `[1, panic("no")]` is a `list(int)`.

## Open questions

- **Arguments after one that never returns.** In `f(panic("a"), g())`, `g()` never runs. The `unreachableCode` rule
  only looks at the statements of blocks.
- **Inferred divergence.** A function that never returns but is not annotated, such as `def loop = { x -> loop(x) }`,
  is inferred as `a -> b`, not `a -> undefined`, so calls to it are not seen as diverging.
- **Other `Equal` constraints.** Whether they should let `undefined` through too is open.
