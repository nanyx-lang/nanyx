---
title: "Attributes"
description: "Metadata annotations such as @entry and @partial"
order: 13
---

Attributes are annotations that start with `@` and modify how the compiler treats the declaration that follows.

# Basic syntax

Place an attribute immediately before the declaration it applies to:

```nanyx fragment
@entry
def main = { ... }
```

```nanyx fragment
@partial module utils
```

A declaration can have one or more attributes, each on its own line. An attribute may also precede a member in a `context` declaration.

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

## `@partial`

Marks a file's module declaration as one fragment of a module split across files. It is written on the declaration's line, and every file of the module must have it.

```nanyx fragment
@partial module utils
```

See [Partial modules](./modules#partial-modules).

## `@customKeywordType(...)`

Says how a custom keyword of a workflow behaves. A workflow builder runs its block under a context whose members are its keywords, and the attribute is placed before the member's declaration in that `context`.

```nanyx fragment
context Async = (
  @customKeywordType(#bind)
  await: [c] (Task(a), ([c] a -> Task(b))) -> Task(b)
)
```

See [Workflows](../advanced/workflows).

## `@baseUnit`

Marks a unit declaration as a base unit of its dimension. Base units name the dimension and are never defined in terms of other units.

```nanyx fragment
@baseUnit unit(s) Seconds: Time
```

See [Units and dimensions](../work-in-progress/units-and-dimensions), which is currently a design proposal.

# Notes

- Attributes are compile-time metadata.
- Their meaning is defined by the compiler and language features that consume them.
- Prefer using documented attributes and patterns from the language guides.
