---
title: "Shipped packages"
description: "How the packages the compiler ships are built and found"
order: 11
---

The packages the compiler ships, the console host among them, are described for users in
[Hosts](../advanced/hosts#packages-the-compiler-ships). This page says how they are built and found. What is left,
the official linter as a package, is on [Built-ins as packages](../work-in-progress/built-ins-as-packages) in Work in
progress.

# How shipped packages are built

- **Shipped packages are source directories.** `packages/` at the repository root holds them, each named for its
  package, with its manifest at the compiler's version and its sources under `src`: `packages/nanyx.console`,
  `packages/nanyx.console.core`, `packages/nanyx.fs`, `packages/nanyx.http.core`, `packages/nanyx.http`,
  `packages/nanyx.json` and `packages/nanyx.regex`. Every build of the compiler copies the directory beside it (a `None` item in
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
- **Regular expressions are Nanyx code.** `nanyx.regex` is written over the standard library, with no intrinsic of its
  own: `regex.syntax.nyx` reads a pattern into a tree, applying its flags as it goes, and `regex.vm.nyx` compiles the
  tree into a Thompson NFA and runs it as a Pike VM, whose threads are kept in priority order and marked in an
  `MArray` per code point, so matching is linear in the text for every pattern. The design is on
  [Regular expressions](../work-in-progress/regex).
