---
title: "Operator contexts"
description: "How an operator finds its context in the prelude, and how a function requires one"
order: 9
---

> **Status: implemented for the arithmetic operators and `++`.** `Nanyx.Compiler/OperatorContexts.fs` finds an
> operator's context, `ArithmeticInference.fs` infers the requirement for a function that does not write its type, and
> `InlineContexts.fs` reads a requirement written as a member. What is left is under [Not done yet](#not-done-yet).

# The idea

A context is a record type, and an operator on a value of a type variable reads one member of one. So the compiler
keeps no table of which operator belongs to which context. An operator reads a member, and **its context is the
prelude's context with the fewest members that has that member**.

| Syntax | Member it reads |
|---|---|
| `a + b`, `a - b`, `a * b`, `a / b`, `a % b` | the member of the operator's name: `` `+` ``, `` `-` ``, ... |
| `-a` | `negate` |
| `a ++ b` | `` `++` `` |
| `a == b`, `a != b` | `` `==` `` |

`Add(a)` has four members and `Num(a)` eight, and both have a `` `+` ``, so `+` is read through `Add`. Only `Num` has
a `` `*` ``, so `*` is read through `Num`. `` `++` `` is the one member of `Append(a)`, so `++` is read through it.

## `++` and `Append`

`++` on a `string` or a `list` is the built-in operator, as `+` on an `int` is. The prelude attaches `` string.`++` ``
and `` list.`++` ``, which are what a function that requires `Append` is passed, and a type gets an `Append` by
attaching `` `++` ``, as `` def $Path.`++` = ... `` does.

```nanyx
def concat: [Append(a)] a, a -> a = { x, y -> x ++ y }   -- concat("a", "b"), concat([1], [2])
def join = { x, y -> x ++ y }                             -- [Append(a)] a, a -> a
```

- `def concat: a, a -> a = { x, y -> x ++ y }` is `NYX0205`: its type must require `Append(a)`.
- `concat(1, 2)` is `NYX0205` at the call: there is no `` int.`++` ``.
- `1 ++ 2`, on a type known to have no `++`, is `NYX0201`.
- Where an unannotated function cannot require `Append` (the two cases below), its `++` is left as it was: the
  operands are not made ints, and a type the backend cannot join fails there with `NYX0301`, as before.

Several operators on values of one type need the context of each, less any whose members another of them has:

- `+` and `*` need `Num(a)` alone, since a `Num` has every member an `Add` has;
- `+` and `==` need `Add(a)` and `Eq(a)`, since no one context has both.

Only the prelude's contexts are looked at. A context a program declares with a `` `+` `` member is still a context,
and a function can still require it by name, but no operator is read through it unless the function does.

# Functions that do not write their type

A top-level function that does not write its type requires the contexts its operators need, as it already did for
`==` and `<`:

```nanyx
def add = { x, y -> x + y }                 -- [Add(a)] a, a -> a
def lerp = { a, b, t -> a + (b - a) * t }   -- [Num(a)] a, a, a -> a
def neg = { x -> -x }                       -- [Add(a)] a -> a
```

Each can then be called with ints, floats, or any type that attaches the members.

The requirement is inferred only for an operand whose type is one of the function's own type variables once the
whole function is inferred. Anything that makes the operand a number does so as before:

```nanyx
def inc = { x -> x + 1 }       -- int -> int: `1` is an int
def half = { x -> x / 2.0 }    -- float -> float
```

A literal is still an `int` or a `float` in a function that does not write its type (numeric contexts,
[decision 5](./numeric-contexts#decided)), which is what keeps most arithmetic at `int`.

Operators on two of the function's type variables require the contexts of each, and each operator calls the member
of its own variable's instance:

```nanyx
def r = { x, y, p, q -> (x + y, p * q) }          -- [Add(a) & Num(b)] a, a, b, b -> (a, b)
def same2 = { x, y, p, q -> x == y and p == q }   -- [Eq(a) & Eq(b)] a, a, b, b -> bool
```

Two cases keep their operators on ints, as they were, because the requirement could not be used yet:

- **A local function requires no contexts**, so an operator on a type a local function would be generic in is on
  ints: in `def twice = { v -> v + v }` inside another function, `v` is an `int`.

- **A function the module names as a value**, as `add` is in `xs \list.fold(0, add)`. A function that requires a
  parameterised context cannot be a value yet ([#599](https://github.com/nanyx-lang/nanyx/issues/599)), so `add`
  stays `int, int -> int` there. Only the module's own text is looked at: an exported function another module
  passes as a value has its requirement inferred, and that module fails in the backend, as #599 describes. An
  exported function should write its type.

This replaces the numeric contexts page's "unannotated arithmetic is unchanged": `{ x, y -> x + y }` was
`int, int -> int`.

## How it is inferred

`ArithmeticOperators.check` leaves an operator on a type that is still a variable pending
(`InferState.PendingArithmetic`) instead of making its operands ints. When the function around it is generalised
(`ArithmeticInference.generalize`):

- an operator on one of the function's own type variables stays, unless one of the two cases above applies, and
  `ArithmeticInference.require` turns it into a call that requires its context of that variable, which `ContextInference.resolvePending` makes the function
  require, as it does for a call to a function that requires a context. The operator then calls the member of
  that instance (see [Two instances of one context](#two-instances-of-one-context));
- any other operand is constrained to `int` (or is already a `float`), and the function is generalised again;
- a local def does the second for the operators on types it would be generic in, and leaves the rest to the
  function around it.

# Two instances of one context

A function can require two contexts with a member of the same name, of different type variables:

```nanyx
def both: [Add(a) & Add(b)] a, a, b, b -> (a, b) = { x, y, p, q -> (x + y, p + q) }
```

Both `` `+` ``s are bound to the name in the body, so the name alone does not say which an operator calls. An
operator on values of a type variable calls the member of the nearest instance in scope of a context of that
variable that has it (`OperatorCalls.definition`): `x + y` calls `Add(a)`'s, and `p + q` calls `Add(b)`'s. The
operator is recorded with that instance (`InferState.OperatorInstances`), and written for the backend as a call
through it, `` Add(a).`+`(x, y) `` (`ExpressionRewrites.callOperators`), which the backend finds by the instance's
key. An operator in a function whose requirement is inferred is recorded with the instance it found in the same way.

A call to a member by its name, `show(x)`, still finds whichever is bound last when two contexts have a member of
that name.

# Requiring an operator by its member

A function's type can require an operator by the member it reads, rather than by the context's name:

```nanyx
def add: [`+`: a, a -> a] = { + }
def scale: [`+`: a, a -> a & `*`: a, a -> a] a, a -> a = { x, k -> x * k + x }
```

This is not a context of its own. Before the def is checked, each such requirement is read as the prelude's
context that has the member, at the type the member's type is written for. So `add` is `[Add(a)] a, a -> a` and
`scale` is `[Num(a)] a, a -> a`, exactly as if they said so: a call passes the whole instance, hovers show the
context's name, and the body can use the context's other members.

- The member's type is a list of types, as a def's is: `a, a -> a` and `(a, a) -> a` are the same.
- It must be the member's type in the context: `` [`+`: a -> a] `` is `NYX0201`.
- Only an operator can be required this way. `[toString: a -> string]` is `NYX0108`, which says to write
  `[ToString(a)]`.
- Only members of the prelude's contexts can. `` [`<`: a, a -> bool] `` is `NYX0108`, since `<` reads `Ord`'s
  `compare`, and the message says to write `[Ord(a)]`.
- Requirements written both ways are merged: ``[Add(a) & `+`: a, a -> a]`` is `[Add(a)]`.
- A def that writes only its contexts, as `add` above does, is given the type it would have written, so a call can
  read the context's argument from it. This also makes `def sub: [Add(a)] = { x, y -> x - y }` callable, which it
  was not (`ContextInference.writtenType`).

The formatter writes the member's type without brackets: `` [`+`: a, a -> a] ``.

# The rule for the prelude

Because an operator's context is found by size, **every member of the prelude's contexts must have exactly one
smallest context**. Two contexts of the same size with the same member, and no smaller context that has it, would
leave an operator that reads it with no context.

The compiler checks this when it checks the prelude (`OperatorContexts.ambiguityErrors`), and reports `NYX0006` at
the context, so a prelude that breaks the rule fails every build and every test. When adding a context to the
prelude, or a member to one:

- a new context that repeats an operator of an existing one must have more members than it (as `Num` has more than
  `Add`), or fewer, in which case the operator moves to it for every function whose type is inferred;
- giving a context a member changes its size, which can change which context an operator is read through;
- if neither is wanted, give the operator a context of its own.

# Not done yet

- **`==`, `<` and the other comparisons are still found by name** (`WellKnownContexts`), not through
  `OperatorContexts`. They need more than the member: `<` reads `compare`, and both have instances the compiler
  derives from structure. They are inferred for a function that does not write its type already.
- **Requiring `<` or `!=` by member.** Each would have to be read as the member it stands for.
- **A local function that is generic in its arithmetic.** It would need local functions to require contexts
  ([#550](https://github.com/nanyx-lang/nanyx/issues/550)). Until then a local def that writes a requirement,
  by member or by name, type-checks and fails in the backend with `NYX0301`.
- **Functions named as values**: they wait on [#599](https://github.com/nanyx-lang/nanyx/issues/599). When that
  lands, the fallback in `ArithmeticInference.generalize` (`namedAsValue`) goes.
- **Calling one of two members of one name by its name.** An operator on a type variable calls its variable's
  instance's member, but a call written with the member's name has only the name to go by. The plan is to write
  the instance, `Log(string).write(...)`.
- **Inline requirements outside a def's type**: in a lambda parameter's type or a type definition they are not read.
