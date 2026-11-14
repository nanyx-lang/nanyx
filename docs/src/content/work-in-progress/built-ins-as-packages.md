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

**The console host** is already written in Nanyx ([Hosts](./hosts)), but it lives in the embedded standard library as
`nanyx/console.host`, with `Console`, `Env` and `HostRuntime` beside it. The compiler knows it by name. `Hosts.fs` has a
table of hosts, all of them standard-library modules, the default host is in that table, and `HostRuntime` is provided
only to a module that is.

# Where they're going

**The official linter is a Nanyx package**, for example `nanyx.lint`. It's written against the same public contract
any linter uses, and it reports the same diagnostics, codes and fixes as today's rules. The compiler keeps the linter
host: loading linter packages, severities, configuration and suppression. The rules themselves go.
([#1343](https://github.com/nanyx-lang/nanyx/issues/1343))

**The console host is a package**, for example `nanyx.console`, with `Console` and `Env`. Any module that exports a host
`run` is a host, from any package. `HostRuntime` goes to the module the manifest's `[host]` (or `--host`) names, rather
than to a list the compiler keeps. The table of hosts in `Hosts.fs` goes. The standard library keeps only what every
program needs, whatever its host. ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344))

**Shipped, but not special.** The compiler's distribution includes both packages, built and pinned to its release, so
an offline install with nothing downloaded builds, runs and lints a program. They are not defaults either: a package
uses a linter or a host only when its manifest names it. `nanyx new` writes both lines into a new project, so a new
project is linted and runs from the start. The lockfile records them like any other package.
([#1342](https://github.com/nanyx-lang/nanyx/issues/1342))

# The manifest

Decided by the maintainer. The manifest has a small, fixed set of top-level sections, and a package's settings sit
under its own entry, never at the top level, so a section is never mistaken for a package's configuration:

```toml
[package]
name = "emails"
version = "0.1.0"
sources = ["src/**/*.nyx"]

[dependencies]
"acme.web" = "1.2"

[linters."nanyx.lint"]
version = "0.27.0"
lineWidth = 100
rules.unusedBindings = "error"
rules.nonTailRecursion = "off"

[host."nanyx.console"]
version = "0.27.0"
```

- **Top-level sections.** `[package]`, `[dependencies]`, `[linters]` and `[host]` are the only ones. Any other is an
  error.
- **Linters are not dependencies.** `[dependencies]` are packages the code imports, which end up in the program. A
  linter runs at compile time and is never imported, like Cargo's build dependencies or npm's dev dependencies, so it
  has a section of its own.
- **Quote a package's name.** A name with a dot is quoted: in TOML, `[linters.nanyx.lint]` would be a table `lint`
  inside a table `nanyx`.
- **A package's settings go under its entry,** with `version`. A linter's rule severities are `rules.<name>`, which
  replaces today's `[linters.default]`. A linter or host declares the settings it accepts, so a misspelt key is an
  error naming the package. For linters that's part of their contract
  ([#107](https://github.com/nanyx-lang/nanyx/issues/107)); for hosts it settles the Hosts page's question on typed
  host configuration. `[host."nanyx.http"]` with `version` and `port = 3000` replaces `[host] use = ...`.
- **The short form** works when there are no settings: `"nanyx.lint" = "0.27.0"` under `[linters]`, as under
  `[dependencies]`.
- **Nothing implicit.**
  - With no `[linters]`, no linter runs.
  - With no `[host]`, a package has no host. `nanyx run` and `nanyx build` refuse it with an error that says to add
    one, while `nanyx check` and library packages need none.
  - `nanyx new` writes both entries, at the compiler's version.
- **Scripts.** A loose script has no manifest. It runs under the console host, and runs the linters its `::linters`
  directives name, if any.

# What it needs

- **Packages that resolve**: local dependencies ([#862](https://github.com/nanyx-lang/nanyx/issues/862)), the
  `.nyxpkg` format and the package cache ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), and resolution
  ([#864](https://github.com/nanyx-lang/nanyx/issues/864)). See [Packaging](./packaging).
- **The public linter contract** ([#107](https://github.com/nanyx-lang/nanyx/issues/107)): how a linter written in
  Nanyx sees the semantic model and reports diagnostics, run on the embedded Wasmtime engine.

# Plan

1. **Ship packages with the compiler** ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)), once the
   `.nyxpkg` format and the cache exist.
2. **The console host as a package** ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)), once packages
   resolve.
3. **The official linter as a package** ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)), once the linter
   contract is designed. The rules move one at a time, with the tests checking that each one reports what it did.

# Open questions

- **What else is built in.** The standard library itself is embedded, and stays the package `nanyx`, versioned with
  the compiler ([Packaging](./packaging#the-standard-library)). Are other parts of it, such as `nanyx/text` or
  `nanyx/math`, better as ordinary packages too, or is "every program needs it" the line?
- **How a shipped package is updated.** Only with a new compiler, or can a program take a newer official linter or
  console host from the package index without upgrading the compiler?
- **The language server.** It runs linters on every edit. Running a linter package compiled to WebAssembly must be
  fast enough for that, which today's F# rules are.
