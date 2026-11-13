---
title: "Contexts vs interfaces"
description: "A practical guide for interface-first developers"
order: 3
---

If you are coming from C#, Java, or TypeScript, Nanyx contexts can feel unfamiliar at first.

A good starting point is: a context is like an interface constraint, but passed implicitly and usable for both typeclass-style capabilities and effects.

# The short version

- **Interfaces** describe object members and are usually implemented explicitly by a type.
- **Contexts** describe required operations and are supplied by `use` or synthesized from attached definitions.
- **Function signatures** declare context requirements directly, so dependencies are explicit in types.

# Mental model for interface users

In interface-heavy languages, you might write:

```text
interface ISummable<T> {
  T zero();
  T add(T other);
}

sum<T>(items: List<T>) where T : ISummable<T>
```

In Nanyx, you model that as a context constraint. The prelude already has one for adding, `Add(a)`:

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)

def sum: [Add(a)] list(a) -> a = { items ->
  items \fold(zero) { + }
}
```

For multiplying and dividing as well there is `Num(a)`, which has every member of `Add(a)` and more, so a function that requires `[Num(a)]` can call `sum`.

The mindset shift here is to think in terms of capabilities (what operations are needed) for a type _which are not required to be intrinsic to the type_. Think of it as "Given a way of summing `a`" rather than "Given a type `a` that **is** `Summable`." A function states what it needs (`Add(a)`), not which concrete type hierarchy it depends on.

This allows capabilities to be "bolted on" to types without modifying their definitions, and for functions to be more flexible in what they can operate on (since abstractions and their implementations don't have to have been defined by the type's author).

# Where contexts differ most

## 1) Implicit provisioning

You can provide implementations with `use`:

```nanyx
use Add(int)(
  zero = 0
  `+` = { a, b -> a + b }
  `-` = { a, b -> a - b }
  negate = { a -> -a }
)

sum([1, 2, 3])
```

This is closer to passing a capability dictionary than calling methods on an instance.

## 2) Can model effects too

Contexts are not only for generic constraints; they also model effects like I/O:

```nanyx
context Console = (
  println: string -> ()
)

def greet: [Console] string -> () = { name ->
  println("Hello, {name}")
}
```

## 3) One pattern for many problems

The same mechanism (`[Context(...)]`) handles:

- effect requirements (`Console`, `FileSystem`, `Memory`)
- generic capabilities (`Add(a)`, `Iter(a)`, etc.)

# Contexts vs interfaces: when to choose what

Use **contexts** when you want:

- behavior selected by capability rather than inheritance
- explicit effect requirements in function types
- generic algorithms over operations (`+`, `next`, `length`, etc.)

Use **records + attached definitions** when you want:

- object-like data with discoverable namespaced behavior
- method-style ergonomics at call sites

In practice, these are complementary: records model data, attached definitions provide behavior, and contexts express what generic/effectful functions require.

# Migration tips

- Start by translating interface constraints to context constraints in generic functions.
- Keep data models as records; avoid recreating deep inheritance trees.
- Prefer small capability contexts (`Iter`, `Sized`, `Sum`) over large “god interfaces.”
- Treat context requirements as part of the API contract.

For full details on contexts, see [Contexts](../advanced/contexts).
