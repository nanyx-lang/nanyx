---
title: "Proposal: Existential types"
description: "Values that hide their type and carry the instances it has, such as a list of things that can each be printed"
order: 3
---

> **Status: idea, not decided.** Nothing on this page is built, and its syntax is a set of options, not a choice. The
> code is proposed syntax, which the compiler does not read.

An existential type is the type of a value that hides its own type and carries the instances that type has. Code that
uses the value knows only that it has those instances, not what it is:

```nanyx fragment
def things: List(any ToString) = [1, "two", 3.5]

for things | t -> println(toString(t))
```

`1`, `"two"` and `3.5` have three different types, but each has a `ToString` instance, so each is an
`any ToString`, and a list can hold them. `toString(t)` calls the instance `t` brought with it.

# Why

A generic type parameter is chosen by whoever uses the type: a `List(a)` holds one `a`, which the caller picks. An
existential's type is chosen by whoever makes the value, and is different for each value. That is what some programs
need:

- **A list of different things that can each do the same thing**: shapes that can each be drawn, steps of a pipeline,
  handlers in a registry.
- **A plugin or a strategy chosen when the program runs**, which the code that calls it can't name the type of.
- **Hiding a representation.** A function can return a value whose type its callers never see, only what they can do
  with it.

Haskell writes it `forall a. Show a => MkShowable a` inside a constructor, Rust `Box<dyn Display>`, Swift
`any CustomStringConvertible`, and C#, Java and Go a value whose type is an interface.

## What Nanyx does today

A record of closures does the same job. The value is captured, and the record holds what can be done with it:

```nanyx fragment
type Printable = (show: () -> string)

def printable: [ToString(a)] a -> Printable = { x -> Printable(show = { toString(x) }) }

def things = [printable(1), printable("two"), printable(3.5)]
```

It works, but:

- **Every context needs a hand-written record and function**, which repeats the context's members with the value
  applied: `Printable` and `printable` for `ToString`, another pair for `Draw`.
- **The value can't be passed to code that requires the context.** A `Printable` isn't an `a` with `ToString(a)`, so
  a generic function such as `[ToString(a)] List(a) -> string` can't take it.
- **Each value allocates a closure for each member.**

# What is not an existential

- **An associated type.** `Num(int)`'s `fraction` is `float` wherever it is written: the instance decides it, and the
  compiler knows it. An existential's type is decided by each value, and nobody else knows it.
- **A [bounded type parameter](./bounded-type-parameters).** `Length(int)` and `Length(float)` are different types,
  and a `List(Length(int))` holds only one of them. The bound limits what the parameter can be; it hides nothing.
- **`List([ToString(a)] a)`.** This looks like it should be the type of the list above, but it can't be. A type
  variable nobody binds is chosen by the caller, so the natural reading is a list of one `a`, which
  `[ToString(a)] List(a)` already says. And `[C]` in front of a type says what a value needs from whoever uses it, as in
  `[Console] int -> int`. An existential value brings its instance with it, the other way round.

# Syntax ideas

## 1. `any` in front of a context

```nanyx fragment
def things: List(any ToString) = [1, "two", 3.5]
def shapes: List(any [Draw, Area]) = [Circle(radius = 1.0), Square(side = 2.0)]
def numbers: any Iter(elem = int) = [1, 2, 3]
```

`any C` is a value of some type `t` with a `C(t)` instance. The hidden type is the context's first parameter, so `any`
only makes sense for a context whose first parameter is a type of values, and several are written as a list. An
associated type is hidden too, unless it is given, as `elem = int` gives it in a context's type today.

It is short, and it is what Swift chose. It can only say "each value hides its own type": it can't say that a whole list
hides one type.

## 2. A binder that says where the type is hidden

```nanyx fragment
def things: List(exists t. [ToString(t)] t) = [1, "two", 3.5]
def sameKind: exists t. [ToString(t)] List(t) = [1, 2, 3]
```

`exists t.` binds `t` where it is written. That says which of two different things is meant: `things` holds values
of different types, and `sameKind` holds values of one type nobody knows. `any ToString` is then short for
`exists t. [ToString(t)] t`.

It is the most precise form, and the longest. It needs a binder for type variables, which Nanyx doesn't have anywhere
else.

## 3. A bound on a type variable that isn't a parameter

```nanyx fragment
type [ToString(t)] Showable = (value: t)
```

The bound is written where a [bounded type parameter](./bounded-type-parameters)'s is, on the left of the `=`. But `t`
isn't a parameter of `Showable`, so each `Showable` hides its own `t`, and carries the `ToString(t)` its bound asks
for. This is what Haskell does. On the right of the `=`, `[ToString(t)]` would be read as the type's context
variable, what its values need when they are used, so the bound can't go there.

It needs no new keyword, and the hidden type has a name, a field and room for more. But the hiding is implicit, and
one forgotten parameter changes the meaning:

```nanyx fragment
type [Num(a)] Point(a) = (x: a, y: a)   -- bounded: Point(int) and Point(float) are different types
type [Num(a)] Point = (x: a, y: a)      -- existential: every Point hides its own a
```

In the second, a point's `x` and `y` share one hidden type, so `p.x + p.y` works, but `p.x + q.x` for two points
doesn't, since their hidden types may differ. A misspelt parameter would hide a type the same way, instead of being
reported. It also makes a nominal type for each use,
so it suits a few named types better than a quick `List(any ToString)`.

## Making and using a value

Whichever is written, these are the same:

- **Making one.** A value becomes an existential where the type expected is one: `[1, "two"]` as a
  `List(any ToString)`, or an argument to a parameter of type `any ToString`. The instance is found there, as it is for
  a call to a function that requires the context, and is an error there if there isn't one.
- **Calling its members.** `toString(t)` on an `any ToString` uses the instance `t` carries. It needs nothing written.
- **Passing it to generic code.** An `any ToString` passed to `[ToString(a)] a -> string` should work, with `a`
  standing for the hidden type and the carried instance passed as the context's. That is what Swift calls implicitly
  opening an existential.
- **Comparing two of them.** Two `any Eq` values can't be compared with `==`: their hidden types may differ. To compare
  them would need a way to check that they are the same type, which is an open question.

# How it would compile

Generic code is [monomorphised](../deep-dives/memory-management#monomorphisation), and a context's members are passed
as closures ([Context calls](../deep-dives/context-calls)). An existential value would be the pair of those: its value,
boxed as a context member's generic values are today (`IrBox`), and the closures of its context's members. A call to a
member is then a call through a closure, as a call to a member that isn't known is now, and a generic function an
existential is passed to is specialised on the boxed type, with its members unknown. It is the same trade as Rust's
`dyn` against generics: one copy of the code, and calls the compiler can't inline.

# Open questions

- **Which syntax**, if any: `any`, a binder, a type variable on the right of a type, or more than one of them.
- **Is it worth a feature?** The record of closures works. A smaller step would be for the compiler to write the record
  and its function for a context, which removes the repetition but not the other limits.
- **Contexts with more than one parameter, and contexts with none.** What `any` means for `Convert(a, b)`, and whether a
  context such as `Console` could be carried at all.
- **Getting the hidden type back**: a pattern that opens an existential and names its type, so that two values can be
  checked to be the same type, or a value cast back to a type it might be.
- **Equality, hashing and printing of the existential itself**, for a `Set(any Hash)` or a `dbg` of one.
