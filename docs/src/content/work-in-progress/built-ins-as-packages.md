---
title: "Built-ins as packages"
description: "The official linter is to be an ordinary Nanyx package, as the console host is"
order: 32
---

The packages the compiler ships, the console host among them, are described in
[Hosts](../advanced/hosts#packages-the-compiler-ships), and how they are built and found in
[Shipped packages](../deep-dives/shipped-packages). This page keeps what is left: the official linter as a package,
and the open questions. It is part of the [packaging](./packaging) workstream.

# Why

- **What's special can't be replaced.** A team that wants different lint rules, or a host for a different platform,
  should be able to write one that works exactly as the official one does. If the official ones use privileges no
  package has, the public contract for linters and hosts is never really tested.
- **Nanyx should be enough to write Nanyx's tools.** A linter written in Nanyx exercises the language, the standard
  library and the [semantic model](./linters#what-a-linter-sees) that every other linter will use.
- **The compiler gets smaller.** Its job becomes running linter packages and hosts, not containing them.

# The official linter

The official linter's rules (`unusedBindings`, `nonTailRecursion`, `unusedResults` and the rest) are F#, in
`compiler/Nanyx.Compiler/Linters.fs`, run by the compiler's linter host. Linters written in Nanyx need their public
contract ([#107](https://github.com/nanyx-lang/nanyx/issues/107), [Linters](./linters#the-public-contract)): how a
linter sees the semantic model and reports diagnostics, run on the embedded Wasmtime engine.

**It will be a Nanyx package**, `nanyx.lint`, written against the same public contract any linter uses, and reporting
the same diagnostics, codes and fixes as today's rules. The compiler keeps the linter host: loading linter packages,
severities, configuration and suppression. The rules themselves go
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)).

**Shipped, but not special.** The compiler's distribution is to include it, built and pinned to its release, as it does
the console host, so an offline install with nothing downloaded builds, runs and lints a program. A package is linted
only when its manifest names a linter, as now, and its version is the compiler's, so it isn't locked. A linter's
settings are to be typed by the linter, as a host's are ([The package manifest](./package-manifest#whats-not-built)).

# Plan

1. **The official linter as a package** ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), once the linter
   contract is designed. The rules move one at a time, with the tests checking that each one reports what it did.

# Open questions

- **What else is built in.** The standard library itself is embedded, and stays the package `nanyx`, versioned with
  the compiler ([Packages the compiler ships](../advanced/hosts#packages-the-compiler-ships)). Are other parts of it,
  such as `nanyx/text` or `nanyx/math`, better as ordinary packages too, or is "every program needs it" the line?
- **How a shipped package is updated.** Only with a new compiler, or can a program take a newer official linter or
  console host from the package index without upgrading the compiler? Shipped packages are source directories beside
  the compiler, which resolution leaves out, so taking one from the index would need resolution to treat it as any
  other package.
- **The language server.** It runs linters on every edit. Running a linter package compiled to WebAssembly must be
  fast enough for that, which today's F# rules are.
