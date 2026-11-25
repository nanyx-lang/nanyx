---
title: "Proposal: Bounded type parameters"
description: "Generic types that exist only for type arguments with certain contexts"
order: 2
---

> **Status: proposed, not built.** The code on this page is proposed syntax, which the compiler does not read yet.

A generic type will be able to say which contexts its type parameters must have. It writes a context list before its
name, on the left of the `=`, as a def's type writes one before its parameters:

```nanyx fragment
type [Num(a)] Length(a) = (base: a)
```

`Length(a)` exists only for an `a` with a `Num` instance: there is a `Length(int)` and a `Length(float)`, but no
`Length(string)`. The list is the type's **bound**. Two things follow from it:

1. **The type is checked where it is written.** `Length(string)` is an error in a signature, an annotation, a field,
   a type argument or anywhere else a type is written, and so is a value that would be one.
2. **The bound is implied.** A def whose type mentions `Length(a)` requires `Num(a)` without writing it, so the two
   signatures below are the same.

```nanyx fragment
def total: list(Length(a)) -> Length(a)
def total: [Num(a)] list(Length(a)) -> Length(a)
```

# Why

[Units and dimensions](./units-and-dimensions) needs this. A dimension is a type generic in its storage, and its
storage always has a `Num`: a function that takes a `Time` is checked as one that requires `Num` of the `Time`'s
storage. As that page stands, the rule belongs to `dimension` alone. A library that wants the same of its own types,
a `Ratio(a)` over any number or a `Histogram(k)` over keys that have an `Ord`, has no way to say it, and the
standard library's types should use the features users have. With bounds, `dimension Time` declares a
`type [Num(a)] Time(a)`, and the rule is this page's.

# Bounds are implied, not stored

A bounded type stores nothing extra. A `Length(int)` is the record `(base: int)` at run time, with no instance in it.

Nothing needs storing because `a` is part of the type. Every signature that holds a `Length(a)` can see `a`, so the
instance comes from where `a` is known, as it does for any `[Num(a)]` function: at a call with a `Length(int)`, the
`Num(int)` is found at the call, and generic code passes its own on. The implied requirement can always be met,
because a `Length(a)` can only exist where `Num(a)` holds.

That is what makes a bound worth writing. A bound that is checked but not implied must be written again on every
function that mentions the type. Haskell's `data Num a => Length a` was like that, and was deprecated for it; Rust's
`struct Length<T: Num>` is like that for every `impl`, which its implied-bounds work sets out to fix. Swift infers a
function's requirements from the generic types in its signature, as this page proposes.

A value that hides its type and carries its instance instead, such as a list of things that can each be printed,
is a different feature (an [existential type](./existential-types)) and not part of this proposal. A record of
functions does that job today:

```nanyx fragment
type Printable = (show: () -> string)

def printable: [ToString(a)] a -> Printable = { x -> Printable(show = { toString(x) }) }
```

# The rules

- **Where the list goes.** On the left of the `=`, before the type's name. A type's context variable, as in
  `type Thunk(a) = [k] protected (...)`, stays on the right: it is what the type's values need when they are used, and
  a bound is what the type's parameters must have.
- **What a bound can name.** Contexts applied to the type's own parameters, one or several, as in
  `type [Num(a), ToString(a)] Gauge(a)` or `type [Convert(a, b)] Bridge(a, b)`. A context that names none of the
  type's parameters, such as `[Console]` or a context variable `[c]`, is an error: it says nothing about the type.
- **Constructing one.** A bounded type's constructor requires its bound: `Length(base = x)` is
  `[Num(a)] (base: a) -> Length(a)`. A record literal checked against a `Length(a)` is the same.
- **Reading one.** Reading a field needs nothing: `l.base` is an `a`. A def that reads it still requires `Num(a)` if
  its type mentions `Length(a)`, as every such def does.
- **Types that hold one.** A type whose definition mentions `Length(a)` with `a` one of its own parameters has the
  bound too, without writing it: `type Span(a) = (start: Length(a), end: Length(a))` is a `[Num(a)] Span(a)`, and
  `Span(string)` is an error that says why.
- **Attached definitions and instances.** A definition attached to a bounded type is checked with the bound in scope,
  so `Length.add: Length(a), Length(a) -> Length(a)` can use `+` on the `base`s. A context instance synthesized from
  attached definitions, as an `Add(Length(a))` is from an attached `zero` and `+`, exists only for an `a` with `Num`.
- **Lambdas and annotations.** A lambda's parameter annotated `l: Length(a)`, and a local def's type, imply the bound
  as a def's type does.
- **Concrete arguments.** `Length(int)` requires `Num(int)`, which the prelude has, so nothing is required of the code
  around it. A type argument without an instance is an error where the type is written, naming the type, its bound
  and the context the argument lacks.
- **No storage in the result alone.** `() -> Length(a)` implies `Num(a)` with nothing at a call to say what `a` is,
  which is already an error ([NYX0236](../advanced/contexts)); the type-argument rule for a type variable only the
  result names (#2531) applies unchanged.

# Seeing the bound

An implied requirement is easy to miss, so the tools show it:

- **Hover** on a def shows its type with the implied contexts written out, marked as implied by the type that gives
  them.
- **An error** about a missing `Num(a)` that a bound implies says which type in the signature implied it.
- **The formatter** leaves signatures as they are written. It neither adds the implied contexts nor removes ones
  written by hand, and writing one by hand is not an error.

# Open questions

1. **Bounds that aren't contexts.** An angle wants any storage with fractions, `F32` or `float`: a storage whose
   `Num`'s `fraction` is itself. That is an equality between types, which a context list cannot write, so
   [Units and dimensions](./units-and-dimensions#open-questions) keeps `dimension Angle(float)` until something can.
2. **Bounds on a context's parameters.** Whether a context, too, can write `context [Ord(k)] Index(k) = (...)`, so
   that an instance of `Index(k)` exists only for keys with an `Ord`, and whether that bound is implied in the same
   way.
3. **Associated types in a bound.** Whether `type [Iter(c)] Walker(c) = (source: c, last: Iter(c).elem)` should let
   a type's fields name an associated type of its bound.
4. **Error codes and wording,** for a type argument outside a bound and a bound that names none of the type's
   parameters.

# Related

- [#2788](https://github.com/nanyx-lang/nanyx/issues/2788): contexts in front of a bare type variable, as in
  `(val: [ToString(a)] a)`, are accepted and do nothing. That syntax reads as a requirement on using the value, not as
  a bound, and should be an error.
- [Units and dimensions](./units-and-dimensions), whose storage rule this generalises.
