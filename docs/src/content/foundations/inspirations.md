---
title: "Inspirations"
description: "The languages whose ideas Nanyx builds on, and what it took from each"
order: 2
---

Very little in Nanyx is new. Most of it is an idea someone else had first, worked out carefully and shown to be
practical, which Nanyx then put together in its own way. This page says where those ideas came from, grouped by the
feature they shaped, and thanks the people who made them.

# Tag unions

**OCaml's polymorphic variants** and **Roc's tag unions**.

A tag such as `#some(42)` needs no declaration: it is a value of every union that includes it, and a function can
accept an open union, `#some(a) | r`, and pass on the tags it doesn't handle. OCaml's polymorphic variants showed that
structural, open unions work in a language with full type inference. Roc showed how pleasant they are to use as a
language's main way to say "one of these", and gave Nanyx the idea of tags carrying their payloads directly.

In early Nanyx a tag was a singleton value, and a payload meant a tuple: `type X = #one | (#two, string)`, from when
Nanyx was meant to support arbitrary unions of types. Roc's design is why it's `#two(string)` today. See
[Tags](../tour/tags).

# Contexts

**Haskell's type classes**, **Koka's** and **Flix's effects**, **Scala 3's givens**, and the wider work on effect
programming.

A context is a set of operations a function requires, such as `Console`, or `Eq(a)` for a type's equality, that is
provided where the function is called.

Contexts owe most to the research on **algebraic effects and handlers**: the idea, from Plotkin and Pretnar's work
and languages such as Eff, Koka, Effekt and Flix, that what a function may do belongs in its type, separately from
what it computes, and that the code which runs it decides how those effects are carried out. Nanyx's contexts are a
deliberately simple corner of that design (no resumable handlers), but the shape is the same: a pure function's type
says it's pure, and an effect is a requirement passed in, not something done behind the program's back.

- From **Haskell**: contexts over types (`Eq(a)`, `Ord(a)`, `Hash(a)`) as type classes, and *coherence*. A type has one
  instance of a context, its own, which nothing can replace, so a set built in one place can't be read with a
  different equality in another.
- From **Koka**: effects tracked in function types (`[Console] string -> ()`), passed through higher-order functions
  with a variable (`[c]`, Koka's effect-row polymorphism), and handlers that are ordinary functions running a block
  under an implementation (`withConsoleLog { ... }`).
- From **Flix**: effects as part of every function's type in a practical, everyday language, with purity tracked by
  the compiler and inferred where it isn't written, and effect polymorphism for higher-order functions, so a pure
  `list.map` stays pure when given a pure function.
- From **Scala 3**: capabilities passed implicitly to whatever needs them, written once where they're provided.

See [Contexts](../advanced/contexts).

# Mutation that stays in its scope

**Haskell's `runST`** and **Flix's regions**.

`memory { ... }` gives a block mutable cells and collections that can't escape it, so a function that uses mutation
inside can still be pure from outside. Haskell's `runST` showed that a type can keep local mutation local; Flix's
regions showed how to make it a natural part of the language, with a scope for each block. Nanyx's scoped contexts are
that idea, with the scope implied rather than written. See [Memory](../advanced/memory).

# Memory management

**Koka's Perceus** and **Lean 4**.

Nanyx has no garbage collector: values are reference-counted, and freed as soon as nothing refers to them.

- From **Koka**: Perceus, precise reference counting with reuse. A cell taken apart and rebuilt to the same size is
  reused in place, which is what makes immutable data fast. And **functional but in place**: code that rebuilds a
  value nobody else can see costs no more than changing it (see [Principles](./principles#functional-but-in-place)).
- From **Lean 4**: inferring which parameters a function only borrows, so reading a shared value counts nothing, and
  updating an array in place when it's held only once.

See [Reference counting](./reference-counting).

# Workflows and pipes

**F#**.

Nanyx's workflows (`proc { try ... }`, with custom keywords) are F#'s computation expressions, rebuilt on contexts. The
pipe, `xs \map(f)`, is F#'s `|>`, and much of Nanyx's feel comes from F#: indentation-sensitive, expression-oriented,
with types inferred and written only where they help. See [Workflows](../advanced/workflows) and
[Piping](../tour/piping).

# Extending a type

**C#'s extension methods**.

An attached definition, `def string.shout = ...`, can be added to any type from any module, as C# and Kotlin allow,
and reads as `"hi" \shout`. Unlike them, it can never replace a definition the type already has. See
[Attached definitions](../advanced/attached-definitions).

# Slices

**C#'s `Span<T>`** and **Swift's `ArraySlice`**.

A slice is a view of part of an array or a string, without copying it. C#'s spans are the reason Nanyx has them;
Swift's `ArraySlice` is why a slice is a type of its own, so a small slice can't quietly keep a large array alive.
Reference counting means a Nanyx slice needs none of a span's restrictions on where it can live. See
[Collections](../tour/collections).

# Hosts

**Roc's platforms** and **the Elm Architecture**.

A Nanyx program runs under a host that supplies its entry point and its capabilities: the console host gives it
`Console` and `Env`, and a web host would give it requests and responses. That's Roc's split between an application
and its platform. A host's shape, a loop of messages and updates with no hidden asynchrony, follows Elm. And Roc
gave Nanyx the name `crash` for stopping a program.

# Packages

**Elm**, **Rust's Cargo**, and **Dart and uv**.

- From **Elm**: semantic versioning checked at publish from the package's public types, rather than trusted to its
  author, and a build that has one version of each package.
- From **Cargo**: caret version ranges, where `"2.1"` means "2.1 or later, before 3.0".
- From **Dart's pub** and **uv**: PubGrub, the version resolver that explains in plain sentences why no set of versions
  fits.

See [Packages](../tour/packages).

# Deferred errors

**Roc**.

A Nanyx program with type errors still runs: the code that checked runs as normal, and the program stops only if it
reaches code that didn't, with the error that code had. So you can try one part of a program while another is still
broken. It had always seemed like something a strongly typed language couldn't reasonably do. Roc showed that it can,
and gave Nanyx the nerve to try it. See [Deferred errors](../work-in-progress/deferred-errors).

# Error messages and tools

**Elm**, **Rust**, and **Go**.

- From **Elm**: compiler errors that explain what was found and what to do about it, without blaming the reader.
- From **Rust**: a stable code for every error, with a page explaining each ([Compiler errors](../standard-tools/errors));
  naming a look-alike Unicode character, such as `−` for `-`, instead of reporting a confusing parse error; and lint
  rules suppressed with an attribute, `@allow(...)`.
- From **Go**: one official layout, applied by [the formatter](../standard-tools/formatter), so nobody argues about
  style.
