---
title: "Shipped packages"
description: "How the packages the compiler ships are built and found"
order: 11
---

The packages the compiler ships, the console host among them, are described for users in
[Hosts](../advanced/hosts#packages-the-compiler-ships). This page says how they are built and found, and why the
official ones are ordinary packages: packages from the package index that come seeded with the compiler
([#2699](https://github.com/nanyx-lang/nanyx/issues/2699)). What is left is in issues: the official linter as a package
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), a host from the index that declares the runtime interfaces
it imports ([#2632](https://github.com/nanyx-lang/nanyx/issues/2632)), and whether parts of the standard library become
packages too ([#2770](https://github.com/nanyx-lang/nanyx/issues/2770)).

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
  `packages/nanyx.cli`, `packages/nanyx.fs`, `packages/nanyx.http`, `packages/nanyx.http.host`, `packages/nanyx.test.host`, and the
  others in `packages/`. Every build of the compiler copies the directory beside it (a `None` item in
  `NanyxCompiler.fsproj` with `CopyToOutputDirectory` and `CopyToPublishDirectory`), so the tests, the language server
  and the published `nanyx` all find it at `ShippedPackages.directory`, and each release archive holds it beside the
  executable. A test checks each shipped manifest's version against `Toolchain.version`, so a release bumps them with
  the compiler.
- **A release publishes them to the index.** Each shipped manifest names this repository, `repository =
  "nanyx-lang/nanyx"`, and the compilers it builds with: a library the one it ships with or a later one, and a host or
  a host library that one only, `=0.54.0`, since they use the compiler's internals. Each release attaches its
  `.nyxpkg` to the `compiler/<version>` release, which the [package index](./package-index) records as it does any
  package's release ([Releases](./releases#the-shipped-packages)).
- **The compiler seeds them into the package cache.** `SeededPackages.seed` packs each shipped package as the release
  packs it, which gives the same bytes on any machine ([The package index](./package-index#the-cache)), unpacks it into
  the cache at `<name>/<version>`, as a download is, and lists it with its SHA-256 in an index of its own in the cache,
  `.shipped/<version>/<fingerprint>` (`PackageStore.shippedIndex`), laid out as the package index is. The fingerprint is
  of the shipped packages' sources (`ShippedPackages.fingerprint`), so a compiler built from other sources seeds its
  own. `PackageIndex.listing` lists the seeded version with those the package index has, and alone when the index
  can't be read, so a build offline finds it. Where the index records the version with another SHA-256, the seeded copy
  is the one listed, with the warning `NYX0303`; where the cache already holds another copy of the version, which is
  never changed, seeding says to delete it. Seeding writes the index beside its place and moves it there, so a process
  never reads one another is still writing.
- **Every store seeds.** A `PackageStore` is made with the function that seeds it, `Seed`: seeding packs, which needs
  modules compiled after the ones that read the store, so it is passed in where a store is made, and a store can't be
  made without one. `SeededPackages.user` is the user's store, which the CLI, the language server and the tests use, and
  `SeededPackages.store` one at a cache and an index of the caller's. `PackageIndex.listing` seeds the store before it
  lists anything, and a build before it reads a package from the index. A process seeds each store once, and packs the
  shipped packages once for every store it seeds; a cache seeded already for this compiler isn't packed again. The test
  projects run with a cache of their own, made empty for each run (`TestPackageCache.fs`, and `TestPackageCache.cs` in
  the language server's), so a test run seeds this build's shipped packages and never meets a copy an earlier build left
  in the user's cache.
- **They are resolved as packages from the index.** A dependency or a host the compiler ships is named at a version,
  resolved by `Resolution` with the index's packages, and locked with its SHA-256. Seeding is the one place that knows
  which packages the compiler ships (`PackageStore.seededCopy`): a locked version that is the one the compiler seeded
  is read from the compiler's own copy in `packages/` (`Package.locked`), which seeding checked holds the same files as
  the cache, so the checked library's modules of it apply and a build under a shipped host checks none of its modules
  from source ([#2702](https://github.com/nanyx-lang/nanyx/issues/2702)); any other version is read from the cache, as
  any package's is. One the lockfile doesn't lock yet is read from the seeded copy, if its requirement accepts that
  version, until the next resolution locks it, which `nanyx pull`, `build` and `run` do without a line for it. A script
  names one with no version, `::install nanyx.fs`, at the version the compiler seeded, and a script with no `::host`
  runs under the console host at it.
- **A seeded package takes the others at their seeded versions.** The shipped packages are built and released together,
  and a host runs its program against the copies of the libraries it was built with, which define the types, such as
  `Console`, the two share. So the seeded version of a shipped package depends on each other shipped package it names
  at that package's seeded version only (`VersionRequirement.exactly`), whatever its manifest writes
  (`Resolution.requirementIn`). Without it, a program under the seeded `nanyx.cli.host` 0.54.0 would lock a `nanyx.cli`
  0.54.0 from the index, whose `nanyx` accepts the compiler, and meet two `Console`s. A package that no shipped host
  runs, such as a library checked alone, takes the newest version its requirements accept.
- **The console host declares itself.** The compiler reads `nanyx.cli.host`'s manifest, as it reads any host's, rather
  than keeping a table of hosts; `HostPackages.console`, read from the compiler's own copy, is the host a loose file runs
  under. The program's package
  records the host's package (`PackageModules.HostPackage`; for a script or a module given as source,
  `ShippedPackages.tryModules`), and the program's modules import the host and its prelude through it, as do the
  host's own files (`PackageModules.resolveWithin`); it needs no `dependencies` entry. A host found by its path is read
  the same way, from its directory. `HostRuntime` goes to the host's file (`Hosts.mayRequireRuntime`, given the files
  that declare the host's module), and a statement in that file isn't a script's. A shipped package's file is named
  after its package in messages, `nanyx.cli.host/src/cli.host.nyx`, wherever the compiler is installed
  (`ShippedPackages.tryDisplay`).
- **The interface package.** `nanyx.cli.host` depends on `nanyx.cli` by version, as any package depends on another
  from the index, and `ShippedPackages.tryModules` reads a shipped package's public modules and the shipped packages it
  depends on, for a loose file, which has no package store and runs under the compiler's own copy of the console host. A file
  of the host's package, or of a package it depends on, sees no host, so the interface package doesn't import the
  prelude that re-exports it. The reference on the `console` and `env` pages of the docs site is generated from its
  doc comments, as the standard library's is ([#1505](https://github.com/nanyx-lang/nanyx/issues/1505), see
  [Standard library implementation](./standard-library-implementation#its-pages-on-the-docs-site)).
- **A host is named `<name>.host`.** A library takes the short name and the host that runs programs against it adds
  `.host` (`Hosts.nameSuffix`); the manifest checker reports a host named otherwise, or a library named so, at its name
  (`NYX0053`, `Manifest.checkHostName`). The shipped packages renamed to this ([#2697](https://github.com/nanyx-lang/nanyx/issues/2697))
  are known by their old names (`RenamedPackages`): a manifest's `host` or `dependencies`, a script's directives, or
  `--host` (through `Package.withHost`) that names one is `NYX0054`, which gives the new name, and an import of
  one of its modules by the old name is `NYX0003` with the import by the new one (`RenamedPackages.inImport`). `nanyx.http`,
  the HTTP host's old name and the library's new one, is one only where a host is named.
- **No host means a library.** `Manifest.Host` and `PackageModules.Host` are options. A package file under no host gets
  no host import, an `@entry` there is `NYX0031` (`Attributes.applyAttributes`), and `nanyx run` on a library says it
  has no entry point.
- **Only what must be shipped.** The compiler ships a package only when a program can't build or run without it: a
  host, its interface packages and the host library they are written on. The linter host, `nanyx.analysis.host`, and
  the package linters are written against, `nanyx.analysis`, are shipped too, since the compiler runs linters through
  them ([Linters](../work-in-progress/linters)). A package that is pure Nanyx over the
  standard library, which nothing in the compiler, a host or the runtime depends on, is an ordinary package from the
  [package index](./package-index), released on its own rather than with every compiler release. `nanyx.json` is one,
  from [nanyx-lang/json](https://github.com/nanyx-lang/json), and so is `nanyx.regex`, from
  [nanyx-lang/regex](https://github.com/nanyx-lang/regex), whose README says how its engine is built and why. It was
  shipped up to 0.39 ([#2631](https://github.com/nanyx-lang/nanyx/issues/2631)); the compiler's tests that use it as
  a large real program run a copy of its sources kept as test data, `Features/Regex/nanyx.regex` in the Wasm tests.
