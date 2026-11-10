---
title: "Attributes"
description: "Metadata annotations such as @entry and @workflow"
order: 13
---

Attributes are annotations that start with `@` and modify how the compiler treats the declaration that follows.

# Basic syntax

Place an attribute immediately before the declaration it applies to:

```nanyx
@entry
def main = { ... }
```

```nanyx
@workflow
def optionChain = (...)
```

A declaration can have one or more attributes, each on its own line. An attribute may also precede a field inside a record literal.

An attribute that takes an argument has it in parentheses straight after its name, written as a tag's payload is: `@customKeywordType(#bind)`. Several values make a tuple and named ones a record. `@entry` takes no argument.

# Common attributes

## `@entry`

Marks the program entry point: the function the runtime calls when the package is run. A package may contain at most one `@entry`.

```nanyx
module main

@entry
def main = {
  println("Hello, World!")
}
```

See [Hello, World!](../getting-started/hello-world) and [Entrypoints](./packages#entrypoints).

## `@workflow`

Marks a workflow builder definition and enables workflow helper generation.

```nanyx
@workflow
def optionChain = (
  try = { ... }
  return = { value -> #some(value) }
  zero = #none
)
```

See [Option chaining](../cookbook/option-chaining) and [Workflows](../advanced/workflows).

## `@customKeywordType(...)`

Used in workflow builders to specify how a custom keyword behaves. It is placed before the field of the builder record that implements the keyword.

```nanyx
def async = (
  @customKeywordType(#bind)
  await = { m, continuation -> ... }
)
```

See [Workflows](../advanced/workflows).

## `@baseUnit`

Marks a unit declaration as a base unit of its dimension. Base units name the dimension and are never defined in terms of other units.

```nanyx
@baseUnit unit(s) Seconds: Time
```

See [Units and dimensions](../work-in-progress/units-and-dimensions), which is currently a design proposal.

# Notes

- Attributes are compile-time metadata.
- Their meaning is defined by the compiler and language features that consume them.
- Prefer using documented attributes and patterns from the language guides.
