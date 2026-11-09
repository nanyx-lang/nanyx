---
title: "What is Nanyx?"
description: "An overview of Nanyx"
order: 1
---

Nanyx is a statically-typed, functional-first programming language designed for expressive workflows, clean syntax, and strong type safety. It combines the best ideas from functional programming with practical features for real-world development, featuring Hindley-Milner type inference, context-based effects, powerful pattern matching, and a unique pipeline operator for composable transformations.

It compiles to WASM, making it ideal for web development, but it's also great for general-purpose programming. Whether you're building a web app, a CLI tool, or a server, Nanyx has you covered.

Nanyx is a modern, type-safe programming language designed for building reliable and maintainable software. It combines the expressiveness of functional programming with the practicality of systems-level control.

# What Nanyx feels like

- **Expression-oriented**: Everything returns a value. No statements, just composable expressions.
- **Records and tags**: Carry the structure of your data with precision and clarity.
- **Pipelines**: Keep data flow obvious with the `\` operator for readable transformations.
- **Type inference**: Rarely write types, but get strong static guarantees.
- **Pattern matching**: Exhaustive matching on records, tag unions, and more.

# Key features at a glance

## Pipeline operator
The `\` operator lets you chain transformations in a readable, left-to-right manner:
```nanyx
data \parse \validate \transform \save
```

## Context-based effects
Manage side effects explicitly through contexts, providing algebraic effect handlers without the complexity:
```nanyx
context Console = (println: string -> ())
def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

## Powerful pattern matching
Exhaustive pattern matching on records, tag unions, literals, and more:
```nanyx
match result
  | #some(value) -> process(value)
  | #error(msg) -> logError(msg)
```

## Strong static typing with inference
Full Hindley-Milner type inference means you rarely need to write type annotations, but you can when it improves clarity.

# Why Nanyx?

- **Type-safe by default**: Catch errors at compile time, not at runtime.
- **Pattern matching**: Elegant control flow with exhaustive checking.
- **Immutable values**: Predictable code that's easy to reason about.
- **Modules**: First-class module system for organizing your code.
- **Fast compilation**: Incremental builds that keep you in the flow.
- **Friendly errors**: Helpful compiler messages that guide you to a fix.

# Who is Nanyx for?

Nanyx is designed for developers who want the safety guarantees of a statically-typed language without sacrificing developer experience. Whether you're building CLI tools, web services, or data pipelines, Nanyx provides the tools you need.

```nanyx
-- Your first taste of Nanyx
println("Welcome to Nanyx!")
```

Ready to get started? Let's [install Nanyx](/docs/getting-started/installation) and write your first program.

# Goals and philosophy

Nanyx is designed with a few guiding goals:

- Be familiar to developers coming from Python or TypeScript, without losing the language's identity.
- Keep syntax minimal and readable.
- Stay strongly typed with inference, so you write fewer annotations.
- Keep iteration fast: the type checker is strict, but a debug build still runs in the face of type errors, so you can exercise the parts of a program that do type check while fixing the parts that don't.
- Prefer consistency over special cases.
- Optimize for expressiveness with fewer features (the "min-gen" principle).
- Inform but do not block: the compiler warns rather than prevents iteration.

This philosophy shows up in error handling. Functions return errors explicitly via tag unions, and the compiler forces you to handle them.

# Productivity focus

Nanyx emphasizes developer experience: rich editor tooling, clear error messages, and fast feedback loops.

Nanyx is built on the principle that code should be **clear, composable, and correct**. It takes inspiration from languages like F#, Haskell, OCaml, and Elm while maintaining its own unique identity.

- **Correctness above all**: Static type checking, exhaustive pattern matching, and no null values ensure fewer runtime errors.
- **Simple ≠ Easy**: The language may take longer to learn, but its consistency pays dividends over time.
- **Inform, don't block**: Compiler errors block a release build, but debug builds can still execute for tight feedback loops. The compiler itself never warns; style and quality checks come from [linters](/docs/advanced/linters).
- **Explicitness over cleverness**: Names are short, types are readable, and workflows are designed to be traced.
