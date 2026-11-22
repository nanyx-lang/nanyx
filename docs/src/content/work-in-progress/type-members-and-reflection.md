---
title: "Type members and reflection"
description: "What is left of type arguments and reflection: JSON for any record, dbg and == as library code, and constructors for decoding"
order: 4
---

Associated types are described on [Contexts](../advanced/contexts#associated-types). Type arguments, inspecting a type,
decoding with `build` and the synthesised `Typed(a)` are described on [Functions](../tour/functions#type-arguments),
and how they compile, with the design behind them and the alternatives considered, in
[Type descriptions](../deep-dives/type-descriptions). This page keeps what is still to be built on them.

# The problem left

Serialization, equality, hashing, printing and mapping to storage all follow a type's structure. A library can walk
a type's description to do them, but the standard ones don't yet. The [JSON cookbook](../cookbook/json-parsing) decodes
field by field, so each field name appears twice:

```nanyx
def decodeUser: string -> Result(User, DecodeError) = { data ->
  data
    \json.decode(
      json.record(name = json.field("name", json.string), age = json.field("age", json.int))
    )
}
```

`dbg`, string interpolation and `==` work on any record because they are compiler intrinsics.

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
| JSON for any record, on reflection | [#1789](https://github.com/nanyx-lang/nanyx/issues/1789) |
| `dbg`, interpolation and `==` as library code (Planning) | [#2000](https://github.com/nanyx-lang/nanyx/issues/2000) |
| Deserialisation-only constructors (Planning) | [#2001](https://github.com/nanyx-lang/nanyx/issues/2001) |
