---
title: "Shipped packages"
description: "How the packages the compiler ships are built and found"
order: 11
---

The packages the compiler ships, the console host among them, are described for users in
[Hosts](../advanced/hosts#packages-the-compiler-ships). This page says how they are built and found, and why the
official ones are ordinary packages. What is left is in issues: the official linter as a package
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), shipped packages as index packages that come
pre-downloaded ([#2699](https://github.com/nanyx-lang/nanyx/issues/2699)), and whether parts of the standard library
become packages too ([#2770](https://github.com/nanyx-lang/nanyx/issues/2770)).

# Why the official ones are ordinary packages

- **What's special can't be replaced.** A team that wants different lint rules, or a host for a different platform,
  should be able to write one that works exactly as the official one does. If the official ones use privileges no
  package has, the public contract for linters and hosts is never really tested.
- **Nanyx should be enough to write Nanyx's tools.** A linter written in Nanyx exercises the language, the standard
  library and the [semantic model](../work-in-progress/linters#what-a-linter-sees) that every other linter will use.
- **The compiler gets smaller.** Its job becomes running linter packages and hosts, not containing them.

# How shipped packages are built

- **Shipped packages are source directories.** `packages/` at the repository root holds them, each named for its
  package, with its manifest at the compiler's version and its sources under `src`: `packages/nanyx.cli.host`,
  `packages/nanyx.cli`, `packages/nanyx.fs`, `packages/nanyx.http`, `packages/nanyx.http.host`, and the
  others in `packages/`. Every build of the compiler copies the directory beside it (a `None` item in
  `NanyxCompiler.fsproj` with `CopyToOutputDirectory` and `CopyToPublishDirectory`), so the tests, the language server
  and the published `nanyx` all find it at `ShippedPackages.directory`, and each release archive holds it beside the
  executable. A test checks each shipped manifest's version against `Toolchain.version`, so a release bumps them with
  the compiler.
- **A release publishes them to the index.** Each shipped manifest names this repository, `repository =
  "nanyx-lang/nanyx"`, and the oldest compiler it builds with, the one it ships with, and each release attaches its
  `.nyxpkg` to the `compiler/<version>` release, which the [package index](./package-index) records as it does any
  package's release ([Releases](./releases#the-shipped-packages)). The compiler still reads them from `packages/`
  beside it: resolving them from the index, as any package is, is the rest of
  [#2699](https://github.com/nanyx-lang/nanyx/issues/2699).
- **The console host declares itself.** The compiler reads `nanyx.cli.host`'s manifest (`HostPackages.tryShipped`)
  rather than keeping a table of hosts; `HostPackages.console` is the host a script runs under. The program's package
  records the host's package (`PackageModules.HostPackage`; for a script or a module given as source,
  `ShippedPackages.tryModules`), and the program's modules import the host and its prelude through it, as do the
  host's own files (`PackageModules.resolveWithin`); it needs no `dependencies` entry. A host found by its path is read
  the same way, from its directory. `HostRuntime` goes to the host's file (`Hosts.mayRequireRuntime`, given the files
  that declare the host's module), and a statement in that file isn't a script's. A shipped package's file is named
  after its package in messages, `nanyx.cli.host/src/cli.host.nyx`, wherever the compiler is installed
  (`ShippedPackages.tryDisplay`).
- **The interface package.** `nanyx.cli.host` depends on `nanyx.cli` by version: a dependency may name a
  package the compiler ships at the compiler's version exactly (`NYX0029` otherwise), as a linter or host may, and
  `ShippedPackages.tryModules` reads a shipped package's public modules and the shipped packages it depends on. A file
  of the host's package, or of a package it depends on, sees no host, so the interface package doesn't import the
  prelude that re-exports it. The reference on the `console` and `env` pages of the docs site is generated from its
  doc comments, as the standard library's is ([#1505](https://github.com/nanyx-lang/nanyx/issues/1505), see
  [Standard library implementation](./standard-library-implementation#its-pages-on-the-docs-site)).
- **A host is named `<name>.host`.** A library takes the short name and the host that runs programs against it adds
  `.host` (`Hosts.nameSuffix`); the manifest checker reports a host named otherwise, or a library named so, at its name
  (`NYX0053`, `Manifest.checkHostName`). The shipped packages renamed to this ([#2697](https://github.com/nanyx-lang/nanyx/issues/2697))
  are known by their old names (`RenamedPackages`): a manifest's `host` or `dependencies`, a script's directives, or
  `--host` (through `HostPackages.tryShipped`) that names one is `NYX0054`, which gives the new name, and an import of
  one of its modules by the old name is `NYX0003` with the import by the new one (`RenamedPackages.inImport`). `nanyx.http`,
  the HTTP host's old name and the library's new one, is one only where a host is named.
- **No host means a library.** `Manifest.Host` and `PackageModules.Host` are options. A package file under no host gets
  no host import, an `@entry` there is `NYX0031` (`Attributes.applyAttributes`), and `nanyx run` on a library says it
  has no entry point.
- **Only what must be shipped.** The compiler ships a package only when a program can't build or run without it: a
  host, its interface packages and the host library they are written on. A package that is pure Nanyx over the
  standard library, which nothing in the compiler, a host or the runtime depends on, is an ordinary package from the
  [package index](./package-index), released on its own rather than with every compiler release. `nanyx.json` is one,
  from [nanyx-lang/json](https://github.com/nanyx-lang/json), and so is `nanyx.regex`, from
  [nanyx-lang/regex](https://github.com/nanyx-lang/regex), whose README says how its engine is built and why. It was
  shipped up to 0.39 ([#2631](https://github.com/nanyx-lang/nanyx/issues/2631)); the compiler's tests that use it as
  a large real program run a copy of its sources kept as test data, `Features/Regex/nanyx.regex` in the Wasm tests.
