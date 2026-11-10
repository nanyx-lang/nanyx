---
title: "Printing values"
description: "Which values can be interpolated into a string, and what dbg prints"
order: 5
---

> **Status: interpolation implemented, `dbg` proposed.** Interpolation goes through `ToString` as [Interpolation through a context](#interpolation-through-a-context) describes ([#330](https://github.com/nanyx-lang/nanyx/issues/330)); the user-facing description is on the [contexts page](../advanced/contexts#tostringa). `dbg` printing any value is not done yet ([#331](https://github.com/nanyx-lang/nanyx/issues/331)). See [Open questions](#open-questions) at the end for what remains open.

Nanyx turns values into text in two places: string interpolation, `"Total: {total}"`, and [`dbg`](../stdlib/io#dbg). They serve different readers, so they follow different rules.

- **Interpolation** makes text for the program's users. A type decides whether it can be shown and how, so interpolation is limited to types that say they have a string representation.
- **`dbg`** makes text for the programmer. It accepts any value and always prints something.

This is the same split as Rust's `Display` and `Debug`, or Python's `__str__` and `__repr__`.

# How it works today

Each interpolation hole is checked through `ToString`, as the rest of this page describes. `dbg` still prints only what the backend can print from a value's type: integers, floats, strings, booleans and tags whose payload is one of those. A function prints as `<function>`.

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
- **The standard library.** `int`, `float`, `bool` and `string` have an attached `toString` in the prelude (see [Converting to text](../stdlib/standard-library#converting-to-text)), so they are not special cases in the type checker: a `ToString(int)` is already synthesized from `Int.toString` today. A hole of one of these types, and of a tag without a payload, uses the compiler's own `ToString`, which the backend writes directly, so `Int.toString` and the others are written as an interpolation, `"{n}"`, without calling themselves. A `use` still comes first.
- **Tags.** A tag with no payload prints as its name, `#readwrite`, as the [tags page](../tour/tags#runtime-behavior) describes. This one is built in. A tag has no name to attach a `toString` to, so a function that requires `ToString` and is called with one, `angled(#red)`, is passed the prelude's `__toString`, a generic function whose hole the backend writes at the type it is specialised for ([#567](https://github.com/nanyx-lang/nanyx/issues/567)).
- **Structures.** A tag with a payload, a tuple, a record and a list get one the compiler derives from their parts, as [Structures](#structures) describes.
- **`use`.** An explicit instance takes priority over synthesis, as it always does, which lets a block change how a type is shown:

```nanyx
def receipt = {
  def pounds: ToString(int) = (toString = { pence -> "£{pence / 100}.{pence % 100}" })
  use pounds
  "Total: {total}"
}
```

## Protected types

A [protected type](../advanced/nominal-and-protected-types) such as `$UserId` can be interpolated only if its module exports a `$UserId.toString`. Outside its module the type has no shape, so there is nothing else to print it from. The module chooses how its values are shown, exactly as it chooses everything else that can be done with them.

## Structures

A tag with a payload, a tuple, a record and a list have a `ToString` when each of their parts has one. The compiler derives it from the type (`Nanyx.Compiler/StructuralInstances.fs`): for each such type a hole is used at, it writes an ordinary definition that writes each part with a hole of its own, so a part is found as any hole's value is, through its attached `toString`, another derived definition, or the compiler's own.

| Value | Written |
| --- | --- |
| `#some("hi")` | `#some(hi)` |
| `#pair(1, "a")` | `#pair(1, a)` |
| `#rect(width = 1.0, height = 2.0)` | `#rect(height = 2.0, width = 1.0)` |
| `(1, "x")` | `(1, x)` |
| `(x = 1, y = 2)` | `(x = 1, y = 2)` |
| `[1, 2, 3]` | `[1, 2, 3]` |

A record's fields are written in alphabetical order, and a string inside a structure is written without quotes, as it is on its own; `dbg` is the one that quotes.

In generic code, a hole of a structure that holds one of the function's type variables requires `ToString` of the variable: `"{#some(value)}"` needs `[ToString(a)]`, and the derived definition is passed the instance the function was. A function that does not write its type has the requirement inferred.

A part with no `ToString`, such as a function or a protected type without a `toString`, is an error at the hole that names the part.

Not done yet ([#630](https://github.com/nanyx-lang/nanyx/issues/630)):

- **Passing a function the `ToString` of a structure with a type variable in it.** Inside a function that requires `[ToString(a)]`, `show(#some(x))` is an error that says to write the value with a hole first; `"{#some(x)}"` works.
- **A local function generic in what it interpolates** cannot interpolate a structure of its parameter: `def wrap = { x -> "{#some(x)}" }` inside another def is an error.
- **A derived `toString` reads only what the top of the module can see**, so a `ToString` of a concrete part loaded by `use` in a block is not used for the part.
- **An empty list literal**, `"{[]}"`, is an error, since its element type is never settled.

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
- **Compound types in generic calls.** A derived `ToString` of a type with a type variable in it can be used by a hole but not yet passed to a function ([Structures](#structures)). Passing it needs an instance built at the call from the instance for the variable.
