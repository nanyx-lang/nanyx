---
title: "Built-ins as packages"
description: "The official linter is built into the compiler today; it will be an ordinary Nanyx package, as the console host already is"
order: 32
---

The console host is already an ordinary package that the compiler ships, described in
[Hosts](../advanced/hosts#packages-the-compiler-ships), and the manifest that names it in
[Packages and projects](../tour/packages#package-manifests), and how shipped packages are built in
[Shipped packages](../deep-dives/shipped-packages) in the Deep dives section. This page keeps what is
left: the official linter as a package, and the open questions. It is part of the [packaging](./packaging)
workstream.

# Why

- **What's special can't be replaced.** A team that wants different lint rules, or a host for a different platform,
  should be able to write one that works exactly as the official one does. If the official ones use privileges no
  package has, the public contract for linters and hosts is never really tested.
- **Nanyx should be enough to write Nanyx's tools.** A linter written in Nanyx exercises the language, the standard
  library and the [semantic model](./linters#what-a-linter-sees) that every other linter will use.
- **The compiler gets smaller.** Its job becomes running linter packages and hosts, not containing them.

# The official linter

**Today** the official linter is F#: the `default` linter's rules (`unusedBindings`, `nonTailRecursion`,
`unusedResults` and the rest) are in `compiler/Nanyx.Compiler/Linters.fs`, about 1,160 lines, run by the compiler's
[linter host](./linters). Linters written in Nanyx aren't possible yet: their contract is
[#107](https://github.com/nanyx-lang/nanyx/issues/107).

**It will be a Nanyx package**, `nanyx.lint`, written against the same public contract any linter uses, and reporting
the same diagnostics, codes and fixes as today's rules. The compiler keeps the linter host: loading linter packages,
severities, configuration and suppression. The rules themselves go
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)).

**Shipped, but not special.** The compiler's distribution includes it, built and pinned to its release, as it does the
console host, so an offline install with nothing downloaded builds, runs and lints a program. It is not a default: a
package is linted only when its manifest names a linter (`linters`, a field of its own, since a linter is never
imported), and `nanyx new` writes the entry. A linter's settings are to be typed by the linter, as a host's are
([The package manifest](./package-manifest#whats-not-built)). Resolution
([#864](https://github.com/nanyx-lang/nanyx/issues/864)) leaves shipped packages out of the lockfile, since their
version is the compiler's.

# What it needs

- **Packages that resolve.** Local dependencies are done ([#862](https://github.com/nanyx-lang/nanyx/issues/862)),
  which is all a shipped source directory needs. The `.nyxpkg` format and the package cache are built
  ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), but shipped packages are still source directories, and
  resolution ([#864](https://github.com/nanyx-lang/nanyx/issues/864)) leaves them to the compiler: taking a newer one
  from the index isn't built. See
  [Packaging](./packaging).
- **The public linter contract** ([#107](https://github.com/nanyx-lang/nanyx/issues/107)): how a linter written in
  Nanyx sees the semantic model and reports diagnostics, run on the embedded Wasmtime engine.

# Plan

Done: shipping packages with the compiler ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)) and the console
host as one of them ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344),
[#1504](https://github.com/nanyx-lang/nanyx/issues/1504)).

1. **The official linter as a package** ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), once the linter
   contract is designed. The rules move one at a time, with the tests checking that each one reports what it did.

# Open questions

- **What else is built in.** The standard library itself is embedded, and stays the package `nanyx`, versioned with
  the compiler ([Packaging](./packaging#the-standard-library)). Are other parts of it, such as `nanyx/text` or
  `nanyx/math`, better as ordinary packages too, or is "every program needs it" the line?
- **How a shipped package is updated.** Only with a new compiler, or can a program take a newer official linter or
  console host from the package index without upgrading the compiler?
- **The language server.** It runs linters on every edit. Running a linter package compiled to WebAssembly must be
  fast enough for that, which today's F# rules are.
