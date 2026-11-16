---
title: "Built-ins as packages"
description: "The official linter and the console host are built into the compiler today; they will be ordinary Nanyx packages, shipped with it but not special"
order: 32
---

Two things that every Nanyx program uses are built into the compiler today: the official linter and the console host.
Both will be ordinary Nanyx packages, written in Nanyx and used like any other. The compiler may ship them, so that a
program needs nothing installed, but it won't treat them specially. It is part of the [packaging](./packaging)
workstream.

# Why

- **What's special can't be replaced.** A team that wants different lint rules, or a host for a different platform,
  should be able to write one that works exactly as the official one does. If the official ones use privileges no
  package has, the public contract for linters and hosts is never really tested.
- **Nanyx should be enough to write Nanyx's tools.** A linter written in Nanyx exercises the language, the standard
  library and the [semantic model](./linters#3-the-semantic-model) that every other linter will use.
- **The compiler gets smaller.** Its job becomes running linter packages and hosts, not containing them.

# Where they are today

**The official linter** is F#: the `default` linter's rules (`unusedBindings`, `nonTailRecursion`, `unusedResults` and
the rest) are in `compiler/Nanyx.Compiler/Linters.fs`, about 970 lines, run by the compiler's
[linter host](./linters#4-the-linter-host-internal). Linters written in Nanyx aren't possible yet: their contract is
[#107](https://github.com/nanyx-lang/nanyx/issues/107).

**The console host** is written in Nanyx on [foreign functions](./foreign-functions), with no compiler intrinsics, and
ships with the compiler as a source directory, the package `nanyx.console`, whose manifest declares it a host, with a
prelude that brings `println`, `Console` and `Env` into every program. `Console` and `Env` are in its interface
package, `nanyx.console.core`, which the compiler ships too (see [What's built](#whats-built)). `HostRuntime` is still
in the standard library, and the compiler still names the console host as the one a script runs under, until a script
can name its own ([#1504](https://github.com/nanyx-lang/nanyx/issues/1504)).

# Where they're going

**The official linter is a Nanyx package**, for example `nanyx.lint`. It's written against the same public contract
any linter uses, and it reports the same diagnostics, codes and fixes as today's rules. The compiler keeps the linter
host: loading linter packages, severities, configuration and suppression. The rules themselves go.
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343))

**The console host is a pair of packages** ([Libraries for a host](./hosts#libraries-for-a-host)): an interface
package that declares `Console` and `Env`, which libraries depend on, and the host that implements them, which
applications name. The working names are `nanyx.console.core` and `nanyx.console`, while the naming is open. Any
package whose manifest declares it a host is one ([A host package](./hosts#a-host-package)), found by its `path` when
the compiler doesn't ship it. `HostRuntime` goes to the module the manifest's `host` (or `--host`) names, rather than
to a list the compiler keeps. The host's prelude brings `Console`, `Env` and `println` into every module of an
application and of a script, and a library depends on `nanyx.console.core` and imports from it. The standard library
keeps only what every program needs, whatever its host. ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344))

**Shipped, but not special.** The compiler's distribution includes both packages, built and pinned to its release, so
an offline install with nothing downloaded builds, runs and lints a program. They are not defaults either: a package
uses a linter or a host only when its manifest names it. `nanyx new` writes both lines into a new project, so a new
project is linted and runs from the start. The lockfile records them like any other package.
([#1342](https://github.com/nanyx-lang/nanyx/issues/1342))

# The manifest

Decided by the maintainer. The manifest is one Nanyx record written as data ([The package manifest](./package-manifest)).
It has a small, fixed set of top-level fields, and a package's settings sit under its own entry, never at the top
level, so a field is never mistaken for a package's configuration:

```nanyx fragment
(
  package = (name = "emails", version = "0.1.0", sources = ["src/**/*.nyx"])
  dependencies = [(package = "acme.web", version = "1.2")]
  linters = [
    (
      package = "nanyx.lint"
      version = "0.28.0"
      settings = (rules = (unusedBindings = #error, nonTailRecursion = #off))
    )
  ]
  host = (package = "nanyx.console", version = "0.28.0")
)
```

- **Top-level fields.** `package`, `dependencies`, `linters`, `host` and `formatter` are the only ones. Any other is
  an error.
- **Linters are not dependencies.** `dependencies` are packages the code imports, which end up in the program. A
  linter runs at compile time and is never imported, like Cargo's build dependencies or npm's dev dependencies, so it
  has a field of its own.
- **A package's settings go under its entry,** beside `package` and `version`, in `settings`. A linter's rule
  severities are `settings = (rules = (...))`. A linter or host declares the settings it accepts, so a misspelt key is
  an error naming the package. For linters that's part of their contract
  ([#107](https://github.com/nanyx-lang/nanyx/issues/107)); for hosts it settles the Hosts page's question on typed
  host configuration. `host = (package = "nanyx.http", version = "...", settings = (port = 3000))` names a host and
  configures it.
- **An entry without settings** is just `(package = "nanyx.lint", version = "0.28.0")`.
- **Nothing implicit.**
  - With no `linters`, no linter runs.
  - With no `host`, a package is a library. An `@entry` in it is an error at the `@entry` (`NYX0031`), from `nanyx check`,
    `nanyx run` and `nanyx build` alike, and running it is a fatal `no entry point` error. Done (see
    [What's built](#whats-built)).
  - `nanyx new` writes both entries, at the compiler's version.
- **Scripts.** A loose script has no manifest. It runs the linters its `::linters` directives name, if any, names its
  host with `::host`, as a manifest's `host` does, and the packages it imports from with `::dependencies`, as a
  manifest's `dependencies` does: `::host.acme.server = { path = "../server" }`,
  `::dependencies.nanyx.console.core = "0.28.0"`. Without `::host` it runs under the console host the compiler ships.
  Done ([#1504](https://github.com/nanyx-lang/nanyx/issues/1504)).

# What it needs

- **Packages that resolve**: local dependencies are done ([#862](https://github.com/nanyx-lang/nanyx/issues/862)),
  which is all a shipped source directory needs. Shipping them as `.nyxpkg` waits for the format and the package cache
  ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), and taking a newer one from the index waits for resolution
  ([#864](https://github.com/nanyx-lang/nanyx/issues/864)). See [Packaging](./packaging).
- **The public linter contract** ([#107](https://github.com/nanyx-lang/nanyx/issues/107)): how a linter written in
  Nanyx sees the semantic model and reports diagnostics, run on the embedded Wasmtime engine.
- **A host that needs no intrinsics.** Done: the console host is written on typed foreign functions and host memory
  ([Foreign functions](./foreign-functions), [#1426](https://github.com/nanyx-lang/nanyx/issues/1426)).

# Plan

1. **Ship packages with the compiler** ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)), as source
   directories in the distribution (`.nyxpkg` once [#863](https://github.com/nanyx-lang/nanyx/issues/863) lands), each
   named at the compiler's version. A manifest names a shipped package at that version exactly: anything else is
   `NYX0029`, naming the version shipped. Done, with the console host as the first (see [What's built](#whats-built)).
2. **The console host as a package** ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)), as the first shipped
   source directory. Its manifest declares it a host, with a prelude, and a host the compiler doesn't ship is found by
   its path, and `Console` and `Env` are in the interface package `nanyx.console.core`: done (see
   [What's built](#whats-built)). A script's `::host` and `::dependencies` directives are done too
   ([#1504](https://github.com/nanyx-lang/nanyx/issues/1504)).
3. **The official linter as a package** ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), once the linter
   contract is designed. The rules move one at a time, with the tests checking that each one reports what it did.

# What's built

- **Shipped packages are source directories.** `packages/` at the repository root holds them, each named for its
  package, with its manifest at the compiler's version and its sources under `src`: `packages/nanyx.console`, with
  `console.host`. Every build of the compiler copies the directory beside it (a `None` item in `NanyxCompiler.fsproj`
  with `CopyToOutputDirectory` and `CopyToPublishDirectory`), so the tests, the language server and the published
  `nanyx` all find it at `ShippedPackages.directory`, and each release archive holds it beside the executable. A test
  checks each shipped manifest's version against `Toolchain.version`, so a release bumps them with the compiler.
- **The console host declares itself.** `nanyx.console`'s manifest has `host = (root = "console.host", prelude =
  "console.prelude")` in its `package`, and the compiler reads it (`HostPackages.tryShipped`) rather than keeping a
  table of hosts; `HostPackages.console` is the host a script runs under. Its prelude, `console.prelude`, re-exports
  `Console`, `IoError`, `print`, `println`, `eprint` and `eprintln` from `nanyx.console.core/console`, and `Env` from
  `nanyx.console.core/env`.
  The program's package records the host's package (`PackageModules.HostPackage`; for a script or a module given as
  source, `ShippedPackages.tryModules`), and the program's modules import the host and its prelude through it, as do
  the host's own files (`PackageModules.resolveWithin`); it needs no `dependencies` entry. A host found by its path is
  read the same way, from its directory. `HostRuntime` goes to the host's file (`Hosts.mayRequireRuntime`, given the
  files that declare the host's module), and a statement in that file isn't a script's. A shipped package's file is
  named after its package in messages, `nanyx.console/src/console.host.nyx`, wherever the compiler is installed
  (`ShippedPackages.tryDisplay`).
- **The interface package.** `packages/nanyx.console.core` declares `Console`, `IoError`, `print`, `println`, `eprint`
  and `eprintln` in its module `console`, and `Env` in `env`; they left the standard library, so `import nanyx/console`
  is an unknown module now. `nanyx.console` depends on it by version, as `dependencies = [(package =
  "nanyx.console.core", version = "0.28.0")]`: a dependency may name a package the compiler ships at the compiler's
  version exactly (`NYX0029` otherwise), as a linter or host may, and `ShippedPackages.tryModules` reads a shipped
  package's public modules and the shipped packages it depends on. A file of the host's package, or of a package it
  depends on, sees no host, so the interface package doesn't import the prelude that re-exports it. A library lists
  `nanyx.console.core` in its `dependencies` and imports `nanyx.console.core/console`; an application may too, to
  import it under an alias. The reference on the `console` and `env` pages of the docs site is kept by hand until the
  generator covers shipped packages ([#1505](https://github.com/nanyx-lang/nanyx/issues/1505)).
- **No host means a library.** `Manifest.Host` and `PackageModules.Host` are options. A package file under no host gets
  no host import, an `@entry` there is `NYX0031` (`Attributes.applyAttributes`), and `nanyx run` on a library says it
  has no entry point. A loose script still runs under the console host.

# Open questions

- **What else is built in.** The standard library itself is embedded, and stays the package `nanyx`, versioned with
  the compiler ([Packaging](./packaging#the-standard-library)). Are other parts of it, such as `nanyx/text` or
  `nanyx/math`, better as ordinary packages too, or is "every program needs it" the line?
- **How a shipped package is updated.** Only with a new compiler, or can a program take a newer official linter or
  console host from the package index without upgrading the compiler?
- **The language server.** It runs linters on every edit. Running a linter package compiled to WebAssembly must be
  fast enough for that, which today's F# rules are.
