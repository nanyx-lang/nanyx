---
title: "Built-ins as packages"
description: "The official linter is built into the compiler today; it will be an ordinary Nanyx package, as the console host already is"
order: 32
---

The console host is already an ordinary package that the compiler ships, described in
[Hosts](../advanced/hosts#packages-the-compiler-ships), and the manifest that names it in
[Packages and projects](../tour/packages#package-manifests). This page keeps what is left: the official linter as a
package, how shipped packages are built, and the open questions. It is part of the [packaging](./packaging)
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
([The package manifest](./package-manifest#whats-not-built)). Once versions are chosen and the lockfile is written
([#864](https://github.com/nanyx-lang/nanyx/issues/864)), it records shipped packages like any other; today it
records none.

# What it needs

- **Packages that resolve.** Local dependencies are done ([#862](https://github.com/nanyx-lang/nanyx/issues/862)),
  which is all a shipped source directory needs. The `.nyxpkg` format and the package cache are built
  ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), but shipped packages are still source directories, and
  taking a newer one from the index waits for resolution ([#864](https://github.com/nanyx-lang/nanyx/issues/864)). See
  [Packaging](./packaging).
- **The public linter contract** ([#107](https://github.com/nanyx-lang/nanyx/issues/107)): how a linter written in
  Nanyx sees the semantic model and reports diagnostics, run on the embedded Wasmtime engine.

# Plan

Done: shipping packages with the compiler ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)) and the console
host as one of them ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344),
[#1504](https://github.com/nanyx-lang/nanyx/issues/1504)).

1. **The official linter as a package** ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), once the linter
   contract is designed. The rules move one at a time, with the tests checking that each one reports what it did.

# How shipped packages are built

- **Shipped packages are source directories.** `packages/` at the repository root holds them, each named for its
  package, with its manifest at the compiler's version and its sources under `src`: `packages/nanyx.console`,
  `packages/nanyx.console.core`, `packages/nanyx.fs`, `packages/nanyx.http.core`, `packages/nanyx.http` and
  `packages/nanyx.json`. Every build of the compiler copies the directory beside it (a `None` item in
  `NanyxCompiler.fsproj` with `CopyToOutputDirectory` and `CopyToPublishDirectory`), so the tests, the language server
  and the published `nanyx` all find it at `ShippedPackages.directory`, and each release archive holds it beside the
  executable. A test checks each shipped manifest's version against `Toolchain.version`, so a release bumps them with
  the compiler.
- **The console host declares itself.** The compiler reads `nanyx.console`'s manifest (`HostPackages.tryShipped`)
  rather than keeping a table of hosts; `HostPackages.console` is the host a script runs under. The program's package
  records the host's package (`PackageModules.HostPackage`; for a script or a module given as source,
  `ShippedPackages.tryModules`), and the program's modules import the host and its prelude through it, as do the
  host's own files (`PackageModules.resolveWithin`); it needs no `dependencies` entry. A host found by its path is read
  the same way, from its directory. `HostRuntime` goes to the host's file (`Hosts.mayRequireRuntime`, given the files
  that declare the host's module), and a statement in that file isn't a script's. A shipped package's file is named
  after its package in messages, `nanyx.console/src/console.host.nyx`, wherever the compiler is installed
  (`ShippedPackages.tryDisplay`).
- **The interface package.** `nanyx.console` depends on `nanyx.console.core` by version: a dependency may name a
  package the compiler ships at the compiler's version exactly (`NYX0029` otherwise), as a linter or host may, and
  `ShippedPackages.tryModules` reads a shipped package's public modules and the shipped packages it depends on. A file
  of the host's package, or of a package it depends on, sees no host, so the interface package doesn't import the
  prelude that re-exports it. The reference on the `console` and `env` pages of the docs site is generated from its
  doc comments, as the standard library's is ([#1505](https://github.com/nanyx-lang/nanyx/issues/1505), see
  [Standard library implementation](./standard-library-implementation#its-pages-on-the-docs-site)).
- **No host means a library.** `Manifest.Host` and `PackageModules.Host` are options. A package file under no host gets
  no host import, an `@entry` there is `NYX0031` (`Attributes.applyAttributes`), and `nanyx run` on a library says it
  has no entry point.

# Open questions

- **What else is built in.** The standard library itself is embedded, and stays the package `nanyx`, versioned with
  the compiler ([Packaging](./packaging#the-standard-library)). Are other parts of it, such as `nanyx/text` or
  `nanyx/math`, better as ordinary packages too, or is "every program needs it" the line?
- **How a shipped package is updated.** Only with a new compiler, or can a program take a newer official linter or
  console host from the package index without upgrading the compiler?
- **The language server.** It runs linters on every edit. Running a linter package compiled to WebAssembly must be
  fast enough for that, which today's F# rules are.
