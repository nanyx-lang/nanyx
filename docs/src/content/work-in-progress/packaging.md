---
title: "Packaging"
description: "How packages are named, resolved, hosted and published"
order: 32
---

> **Status: proposal.** This page frames the packaging workstream and proposes how packages are named, resolved,
> hosted and published. The manifest format is already described in the docs; the rest is open for discussion
> until the questions at the end are settled. Work is tracked under the `packaging` label.

# Summary

Nanyx should launch with a working package system: a program can depend on a published package by name and
version, `nanyx` fetches and builds it reproducibly, and anyone can publish one. Ecosystems that bolt this on
later (Python, early Go, C and C++) never fully recover from the years without it, so it is designed now, before
the first release, and built alongside the compiler.

The [Packages and projects](../tour/packages) page already describes the user's side:
`.nyxp` and `.nyxproj` TOML manifests, imports of the form `acme.web/http`, `public-modules`, project
references and script mode. This page is about what that page leaves out: who owns a package name, how versions
are chosen, what a published package is, and where it lives.

# Scope

| Area | Where |
|------|-------|
| Manifests | Reading `.nyxp` and `.nyxproj`, checked against the docs |
| Module resolution | Imports by package and module name, `public-modules`, direct-dependency-only imports |
| Version resolution | Choosing one version of each package, the lockfile, error messages when no choice works |
| Package cache | Downloading, verifying and storing packages on the user's machine |
| CLI | `nanyx new`, `add`, `restore`, `build`, `run` (without a file), `publish`, `yank` |
| Package index | The service or repository that maps a name and version to an artifact |
| Publishing | Checks a package must pass before it is accepted, including the API compatibility check |
| Editor | The language server and extension resolving packages the way the CLI does |

Out of scope here:

- Distributing the compiler and the VS Code extension themselves (GitHub releases, Scoop, the Marketplace).
- The standard library's contents: [Standard library](https://github.com/nanyx-lang/nanyx/issues/174). How it is
  *packaged* is in scope.
- Hosts and the contexts they provide: [Hosts](./hosts). Packaging has to
  carry a package's `[host]` table, but what a host is belongs to that design.

# Where the compiler is today

A first slice of `.nyxp` exists ([#279](https://github.com/nanyx-lang/nanyx/issues/279)); the rest is only in the docs.

- **A package can be built and run** (`Package.fs`). The manifest's `[package]` table (`name`, `version`,
  `sources`; `public-modules` is accepted but not used) and `[linters]` table are read, with an error naming the
  key for anything wrong or unknown. `nanyx build`, `nanyx check` and `nanyx run` without a file (or with a
  directory or manifest) check every source file, and `run` runs the one `@entry`; `build` writes it to
  `out/<name>.wat`. `nanyx new` makes a package (`NewPackage.fs`). The language server reads the manifest's linter
  configuration too. `[dependencies]` and `[project-references]` are refused when non-empty, source files need not
  be named modules yet, and there is no `.nyxproj` or `restore`.
  The formatter still reads its `[formatter]` table itself.
- **Imports resolve two ways only** (`resolveImportPath` and `StandardLibrary.tryModuleName` in `Library.fs`).
  A module import `a.b/c` becomes the file `a/b/c.nyx` next to the importing file, with `.` and `/` treated
  alike. A name starting `nanyx/` comes from the standard library embedded in the compiler. There are no search
  paths, and a module's declared name is not checked against the name it was imported by. An import by file path
  must have an alias (`NYX0014`) and must name a script module, not one that declares `module name` (`NYX0015`),
  and no import may name an entrypoint, a file with top-level statements (`NYX0016`), as the
  [modules page](../tour/modules#importing-modules-by-file-path) says (`FileImports.fs`).
- **The parser accepts only part of the documented import syntax.** `import x as m` and grouped imports work;
  `as (a, b)`, `as m & (a, b)` and re-exports (`export collections.list (map, filter)`) do not parse.
- **A program compiles to one WebAssembly module.** Imported modules are type-checked separately and their items
  inlined into the root (`inlinedItemsForImport`), so there is no linking step and no compiled artifact per
  module or package.
- **The standard library is versioned with the compiler.** It is embedded as resources and is described in
  [Standard library implementation](./standard-library-implementation) as "an ordinary package named `nanyx`", which
  is the model this page keeps.

# Goals

- `nanyx add acme.json` followed by `nanyx run` works on a clean machine, and gives the same result on every
  machine that has the same lockfile.
- Publishing a package takes one command and needs no approval from a person.
- A published version never changes and never disappears. It can be yanked, which stops new resolutions picking
  it, but builds that already lock it keep working.
- A minor or patch release cannot break a dependant's build. The publish step checks this from the package's
  exported types, as Elm does, rather than trusting the author's version number.
- A dependency can only do what the contexts it requires allow, and the program's host decides which contexts
  exist. Packaging keeps that guarantee intact: no package gets `$HostRuntime` unless it is the chosen host.
- Running the package system costs close to nothing until the ecosystem is large, and can move to bigger
  infrastructure later without changing the client or invalidating existing lockfiles.

# Proposal

## Names and ownership

A package name is dotted, as in the docs: `acme.web`, `nanyx.http`. The first segment is the package's
**namespace**, and a namespace has an owner. Owners are GitHub accounts at launch (decided in
[#207](https://github.com/nanyx-lang/nanyx/issues/207)).

- A namespace is claimed by a GitHub user or organisation of the same name at first publish, and only that
  account (or members it grants) can publish under it. This reuses an identity people already have and gives
  verified ownership with no account system of our own. Other identity providers can be added later.
- `nanyx` is reserved for packages from the Nanyx project: the standard library (`nanyx`) and official packages
  such as `nanyx.http`.
- Names are case-insensitive for uniqueness and must be lower case. Names that differ only by `-`, `_` or a
  confusable character are refused, to make typosquatting harder.

Every published package has at least two segments (`namespace.name`), so a bare name like `json` can never be
claimed by whoever gets there first.

[Package ownership and locking](./package-ownership-and-locking) proposes
how a claim survives a GitHub rename (it is kept by account ID), how organisations grant publishers, what
`nanyx.lock` records, and dependencies on a single file or a git repository.

## Versions and resolution

- Versions are semantic versions. In a manifest, `acme.core = "2.1"` means "2.1 or later, before 3.0", the
  caret range used by Cargo; an exact or wider range can be written explicitly.
- A build has **one version of each package**. A program is compiled into a single WebAssembly module and types
  from a dependency must unify wherever they are used, so two copies of `acme.core` would mean two incompatible
  sets of types. If a program truly needs two major versions, the new major is published under a new name
  (`acme.core2`), which keeps the rule simple and the cost visible.
- The resolver picks the newest version that satisfies every constraint, using
  [PubGrub](https://github.com/dart-lang/pub/blob/master/doc/solver.md) (as Dart and uv do). Its main attraction is that when
  there is no solution it explains why in plain sentences, which matches the error-message principles.
- `nanyx.lock` records the exact version and SHA-256 of every package in the build. It is written by `restore`,
  `add` and `build`, is committed, and is the only input a build needs from the network.
- Only direct dependencies can be imported, as the docs say. The resolver still sees the whole graph.

## Enforced semantic versioning

Publishing compares the new version's public API with the previous version's: the exported definitions of every
module in `public-modules`, with their types. Removing or changing a definition requires a major version;
adding one requires at least a minor. `nanyx publish` proposes the version number and refuses a wrong one.

This needs the type of every exported definition, which inference already has, and it is one more reason for the
linter rule that asks for annotations on exported definitions
([#104](https://github.com/nanyx-lang/nanyx/issues/104)): an inferred type can change by accident.

## What a package is

A published package is a `.nyxpkg` file: a gzipped tar holding the `.nyxp` manifest, the source files the
manifest selects, the readme and licence, and a generated `api.json` describing the public API.

- Packages ship **source**. The compiler inlines imported modules into one WebAssembly module, so there is no
  compiled form to ship yet, and source works for every compiler version that supports the package's language
  level. A cached compiled form can be added later, as an optimisation the cache owns.
- The manifest gains `nanyx = "0.14"` (the minimum compiler version) and `license`, `description` and
  `repository`, which the index requires.
- `api.json` is what the API comparison and the package website read, so neither has to compile the package.

## Where packages live

The client speaks one small protocol: **a static index served over HTTPS**, like Cargo's sparse index. For each
package there is one JSON file listing its versions, each with the artifact URL, its SHA-256, its dependencies,
its minimum compiler version and whether it is yanked. Because the protocol is just files, what serves them can
change without the client noticing.

**At launch** (decided in [#207](https://github.com/nanyx-lang/nanyx/issues/207)), the index is a GitHub
repository, `nanyx-lang/packages`, in the style of Julia's General registry, Homebrew and our own Scoop bucket:

1. `nanyx publish` builds the `.nyxpkg`, runs the checks locally, and opens a pull request against the index
   repository adding the new version. (It uploads the artifact to a release on the package's own repository, or
   attaches it to the pull request.)
2. A GitHub Actions workflow in the index repository verifies the pull request: the author owns the namespace,
   the version is new, the artifact's hash matches, the package builds with the stated compiler version, and the
   API comparison agrees with the version number.
3. If every check passes, the pull request merges automatically. No person reviews ordinary publishes.
4. On merge, the workflow copies the artifact into storage the project controls, and the index entry points at
   that copy. An author deleting their repository or release cannot break anyone's build.
5. The index is published as static files at a stable address (`packages.nanyx.dev`, via GitHub Pages or a CDN
   in front of object storage). The client never talks to GitHub's API.

Artifacts are stored in object storage with free egress (Cloudflare R2, for example) or as release assets on a
project-owned repository, which costs nothing. The address the client uses is ours either way, so moving between
them later is a redirect.

**Later**, when pull-request publishing becomes the bottleneck, a small service takes over steps 1 to 4 behind an
upload API with tokens, and keeps writing the same static index. The client, the lockfile and every existing
package are unaffected.

Alternatives considered:

- **A registry service from day one** (crates.io, npm). It needs accounts, tokens, moderation tooling, hosting and
  someone on call, before there is an ecosystem to justify it. The static index gets the same client experience
  now and leaves this as an upgrade.
- **Dependencies as git URLs** (Go modules, early Swift). There is no index to run, but names are URLs, which
  contradicts the documented `acme.core = "2.1"`; tags can be moved, so it needs a checksum database anyway; and
  every build depends on the source host staying up.
- **An OCI registry** (GitHub Container Registry). Content-addressed and free for public packages, but names would
  be tied to the registry's paths and every client would need an OCI client. It remains an option for artifact
  storage behind our own URLs.
- **An existing package manager** (npm, NuGet). Its naming, versioning and tooling would leak into Nanyx, and the
  API check could not run at publish.

## The standard library

The standard library stays the package `nanyx`, embedded in the compiler and versioned with it. A manifest does
not list it, and its version is the compiler's. Official packages that should not be tied to the compiler's
release cycle (`nanyx.http`, `nanyx.json`) are ordinary packages in the `nanyx` namespace.

## The cache and offline builds

Downloaded packages are verified against the lockfile and unpacked into a per-user cache
(`~/.nanyx/packages/<name>/<version>/`), shared by every project. A package in the cache is never modified.
`nanyx build --offline` uses only the cache, and a build with a complete lockfile and a warm cache never touches
the network.

## The editor

The language server resolves imports exactly as the CLI does, from the manifest, lockfile and cache. When the
manifest changes, the extension offers to run `nanyx restore`. Go-to-definition into a dependency opens the
cached source read-only.

# Plan

_To do._ Candidate items, not yet filed, roughly in order. Each produces something usable on its own:

1. **Imports by module name within a package.** Read `.nyxp` (`name`, `version`, `sources`,
   `public-modules`), require named modules, check a module's declared name against its import, and resolve
   `package/module` names through the manifest instead of the file system.
2. **The rest of the import syntax.** `as (a, b)`, `as m & (a, b)` and re-exports, which `public-modules` relies
   on (compilation workstream).
3. **Local dependencies and projects.** `.nyxproj`, project references and path dependencies; one version per
   package; only direct dependencies importable; diagnostics for each rule.
4. **The lockfile and the cache.** `nanyx.lock`, the `.nyxpkg` format, verification and the per-user cache, tested
   against a local directory acting as an index.
5. **Resolution.** PubGrub over the index protocol, with error messages for conflicts; `nanyx add` and `restore`.
6. **The index.** Create `nanyx-lang/packages`, the validation workflow, artifact mirroring and the static site;
   reserve the `nanyx` namespace.
7. **Publishing.** `nanyx publish` and `yank`; `api.json`; the API comparison and version check.
8. **Editor support.** Resolve packages in the language server; restore from the extension.
9. **First packages.** Publish at least one official package (`nanyx.json`, say) end to end before launch, to
   prove the whole path.

# Open questions

- **Index address and artifact storage.** The static index on GitHub is decided. Is `packages.nanyx.dev` the
  address, and who holds the domain? Are artifacts kept in object storage (Cloudflare R2) or as release assets on
  a project-owned repository?
- **Resolution.** PubGrub (newest compatible) or minimal version selection (Go: oldest that satisfies, no
  solver, no lockfile strictly needed)? This page proposes PubGrub for its error messages.
- **Major versions.** Is "a new major is a new package name" acceptable, or should a build allow two majors of a
  package whose types never meet?
- **Enforced semver strictness.** Does adding a case to an exported tag union count as breaking (it breaks
  exhaustive matches in dependants)? Elm says yes.
- **Hosts and capabilities.** Should a package declare the contexts it requires in its manifest, so the index can
  show "this package needs `FileSystem`" before anyone installs it?
- **Linters as packages.** [Linters](./linters) wants linters distributed as packages. Are they
  ordinary dependencies, or a separate `[linters]` table resolved the same way?
- **Private packages.** Should the client support more than one index (a company's own) from the start? The
  static protocol makes it cheap: a list of index URLs in user configuration.
