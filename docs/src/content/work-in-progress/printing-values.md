---
title: "Printing values"
description: "Which values can be interpolated into a string, and what dbg prints"
order: 5
---

> **Status: proposal.** Synthesizing a context from attached definitions, which `ToString` relies on, is implemented ([#328](https://github.com/nanyx-lang/nanyx/issues/328)); the rest is not yet. This page records the intended design and the questions that remain open; see [Open questions](#open-questions) at the end. The remaining work is tracked in [#329](https://github.com/nanyx-lang/nanyx/issues/329), [#330](https://github.com/nanyx-lang/nanyx/issues/330) and [#331](https://github.com/nanyx-lang/nanyx/issues/331).

Nanyx turns values into text in two places: string interpolation, `"Total: {total}"`, and [`dbg`](../stdlib/io#dbg). They serve different readers, so they follow different rules.

- **Interpolation** makes text for the program's users. A type decides whether it can be shown and how, so interpolation is limited to types that say they have a string representation.
- **`dbg`** makes text for the programmer. It accepts any value and always prints something.

This is the same split as Rust's `Display` and `Debug`, or Python's `__str__` and `__repr__`.

# How it works today

The type checker accepts any value in an interpolation hole and ignores its type. The WebAssembly backend then decides from the syntax which values it can print: integers, strings, booleans and tags. Anything else type-checks and then fails while generating code, and some values it can print fail because the backend cannot work out their type ([#193](https://github.com/nanyx-lang/nanyx/issues/193)). `dbg` has the same limits.

# Interpolation through a context

A value can be interpolated when its type has a `ToString` context:

```nanyx
context ToString(a) = (
  toString: a -> string
)
```

A hole `"{x}"` is checked as `toString(x)` from `ToString` of the type of `x`, and that call is what runs. At a concrete type the context is found as any context is: loaded with `use`, or [synthesized](../advanced/contexts#context-synthesis). In generic code the hole becomes a requirement in the function's type:

```nanyx
def label: [ToString(a)] (string, a) -> string = { name, value ->
  "{name}: {value}"
}
```

A function that does not write its type gets the requirement inferred, as it would for any other context.

## Where a ToString comes from

- **Attached definitions.** The member is named `toString`, the name the docs already use for it, so any type with an [attached](../advanced/attached-definitions) `toString` gets the context by synthesis and needs nothing else. The `$Money.toString` in the [modules tour](../tour/modules#using-a-module-block-in-its-own-file) makes `"{price}"` work unchanged.
- **The standard library.** `int`, `float`, `bool` and `string` have an attached `toString`, so they are not special cases in the type checker.
- **Tags.** A tag with no payload prints as its name, `#readwrite`, as the [tags page](../tour/tags#runtime-behavior) describes. This one is built in.
- **`use`.** An explicit instance takes priority over synthesis, as it always does, which lets a block change how a type is shown:

```nanyx
def receipt = {
  use ToString(int)(toString = { pence -> "£{pence / 100}.{pence % 100}" })
  "Total: {total}"
}
```

## Protected types

A [protected type](../advanced/nominal-and-protected-types) such as `$UserId` can be interpolated only if its module exports a `$UserId.toString`. Outside its module the type has no shape, so there is nothing else to print it from. The module chooses how its values are shown, exactly as it chooses everything else that can be done with them.

## What cannot be interpolated, for now

Lists, records, tuples and tags with a payload have no `ToString`. Giving `list(a)` one needs an instance built from the instance for `a`, and Nanyx has no instances that depend on other instances (see [structural context synthesis](./type-arguments-and-reflection#structural-context-synthesis)). Until it does, these are an error at the hole:

```
"{scores}": 'list(int)' cannot be interpolated because it has no toString.
Attach one, use `dbg`, or turn it into a string first: scores \map(Int.toString) \String.join(", ")
```

Lifting this later is backwards compatible: it only makes more programs valid.

## What changes in the backend

Each hole is a call to a function the type checker has already chosen, so the backend no longer guesses a hole's type from its syntax. That fixes [#193](https://github.com/nanyx-lang/nanyx/issues/193) for interpolation.

# dbg prints any value

`dbg` keeps its type, `[Console] a -> a`, and needs no `ToString`. After monomorphisation every call to it is at a concrete type, so the compiler generates a printer for each type it is used at, from the type checker's types.

| Value | Printed |
| --- | --- |
| `42`, `1.5`, `true` | `42`, `1.5`, `true` |
| `"Ada"` | `"Ada"`, quoted and escaped |
| `#some(5)` | `#some(5)` |
| `[1, 2]` | `[1, 2]` |
| `(name = "Ada", age = 36)` | `(name = "Ada", age = 36)` |
| `(1, "a")` | `(1, "a")` |
| `()` | `()` |
| a `$Money` | `$Money(250)` |
| a function | `<function int -> string>` |

`dbg` shows what a value is, not how its type chooses to present itself, so it ignores any `ToString`. That keeps its output predictable when chasing a bug.

# Open questions

- **Protected types in `dbg`.** Outside its home module, does `dbg` print a protected value's shape, `$UserId(42)`, or only its name, `$UserId(…)`? Printing the shape breaks no invariant, since invariants are about construction and no code can read what `dbg` prints, but it shows a structure the type means to hide. The current leaning is to print the shape, because `dbg` is a tool for the programmer.
- **The name.** `ToString` matches its member and reads plainly. `Show` (Haskell) and `Display` (Rust) are the alternatives.
- **Formatting.** Do precision and padding belong in the hole, as in `"{price:.2}"`, in separate functions such as `String.padStart`, or in a richer context than `ToString`?
- **Compound types.** When instances can be built from other instances, should lists, records and tuples get a `ToString` automatically, or only when a module asks for one?
