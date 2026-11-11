---
title: "Operator contexts"
description: "How an operator finds its context in the prelude, and how a function requires one"
order: 9
---

> **Status: implemented for the arithmetic operators.** `Nanyx.Compiler/OperatorContexts.fs` finds an operator's
> context, `ArithmeticInference.fs` infers the requirement for a function that does not write its type, and
> `InlineContexts.fs` reads a requirement written as a member. What is left is under [Not done yet](#not-done-yet).

# The idea

A context is a record type, and an operator on a value of a type variable reads one member of one. So the compiler
keeps no table of which operator belongs to which context. An operator reads a member, and **its context is the
prelude's context with the fewest members that has that member**.

| Syntax | Member it reads |
|---|---|
| `a + b`, `a - b`, `a * b`, `a / b`, `a % b` | the member of the operator's name: `` `+` ``, `` `-` ``, ... |
| `-a` | `negate` |
| `a == b`, `a != b` | `` `==` `` |

`Add(a)` has four members and `Num(a)` eight, and both have a `` `+` ``, so `+` is read through `Add`. Only `Num` has
a `` `*` ``, so `*` is read through `Num`.

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

Three cases keep their operators on ints, as they were, because the requirement could not be used yet:

- **A local function requires no contexts**, so an operator on a type a local function would be generic in is on
  ints: in `def twice = { v -> v + v }` inside another function, `v` is an `int`.
- **Operators on two of the function's type variables.** `def r = { x, y, p, q -> (x + y, p * q) }` is
  `int, int, int, int -> (int, int)`. A function's contexts' members are bound by name in its body, so it cannot
  call the `` `+` `` of one instance and the `` `*` `` of another
  ([#690](https://github.com/nanyx-lang/nanyx/issues/690)). The same holds when a call in the function requires a
  context with a member of the same name of another variable, as `list.sum(xs)` does beside `x + y`.
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

- an operator on one of the function's own type variables stays, unless one of the three cases above applies, and
  `ArithmeticInference.require` turns it into a call that requires its context of that variable, which `ContextInference.resolvePending` makes the function
  require, as it does for a call to a function that requires a context. The operator then calls the member;
- any other operand is constrained to `int` (or is already a `float`), and the function is generalised again;
- a local def does the second for the operators on types it would be generic in, and leaves the rest to the
  function around it.

# Requiring an operator by its member

A function's type can require an operator by the member it reads, rather than by the context's name:

```nanyx
def add: [`+`: a, a -> a] = { + }
def scale: [`+`: a, a -> a + `*`: a, a -> a] a, a -> a = { x, k -> x * k + x }
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
- Requirements written both ways are merged: ``[Add(a) + `+`: a, a -> a]`` is `[Add(a)]`.
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
- **Arithmetic on two type variables, and functions named as values**: they wait on
  [#690](https://github.com/nanyx-lang/nanyx/issues/690) and
  [#599](https://github.com/nanyx-lang/nanyx/issues/599). When those land, the fallbacks in
  `ArithmeticInference.generalize` (`clashes`, `namedAsValue`) go.
- **Inline requirements outside a def's type**: in a lambda parameter's type or a type definition they are not read.
