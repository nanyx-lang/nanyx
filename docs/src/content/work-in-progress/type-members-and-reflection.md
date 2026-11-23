---
title: "Type members and reflection"
description: "What is left of type arguments and reflection: JSON for any record, dbg and == as library code, and constructors for decoding"
order: 4
---

Associated types are described on [Contexts](../advanced/contexts#associated-types). Type arguments, inspecting a type,
decoding with `build` and the synthesised `Typed(a)` are described on [Functions](../tour/functions#type-arguments),
and how they compile, with the design behind them and the alternatives considered, in
[Type descriptions](../deep-dives/type-descriptions). This page keeps what is still to be built on them.

# What is built on it

`nanyx.json` 0.2.0 encodes and decodes any record or tag union by walking its type: `json.encode(user)` through
`Typed(a)`, and `json.decode(text, into = type User)`, as the [JSON cookbook](../cookbook/json-parsing) shows
([#1789](https://github.com/nanyx-lang/nanyx/issues/1789)).

# The problem left

`dbg`, string interpolation and `==` work on any record because they are compiler intrinsics. What the JSON walker
can't do yet:

- **A nominal type of your own through `Typed`.** The `typeOf` of a `Typed` instance is written in the prelude, so it
  sees every nominal type, even one of the caller's own module, as `#nominal`: `json.encode(money)` can't write a
  `protected` type of the module that calls it, though `json.decode(text, into = type Money)` reads it
  ([#2382](https://github.com/nanyx-lang/nanyx/issues/2382)).
- **A type that provides a library's context itself.** The design says such a type wins over walking its description,
  but a walker has no way to ask whether a field's type, which it never learns, has an instance, so for now a type is
  written another way by a decoder or encoder built by hand.

# Decisions

Decided by the maintainer on 2026-11-17:

- **`dbg`, interpolation and `==` become library code over descriptions, eventually,** once reflection is proven and
  measured ([#2000](https://github.com/nanyx-lang/nanyx/issues/2000)).

# Open questions

- **Deserialisation-only constructors** for nominal types, which outside their home module are read-only to
  reflection, logged for later ([#2001](https://github.com/nanyx-lang/nanyx/issues/2001)).

# Plan

| Step | Issue |
|---|---|
| JSON for any record, on reflection (done, `nanyx.json` 0.2.0) | [#1789](https://github.com/nanyx-lang/nanyx/issues/1789) |
| `dbg`, interpolation and `==` as library code (Planning) | [#2000](https://github.com/nanyx-lang/nanyx/issues/2000) |
| Deserialisation-only constructors (Planning) | [#2001](https://github.com/nanyx-lang/nanyx/issues/2001) |
