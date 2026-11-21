---
title: "Packaging"
description: "How packages are named, resolved, hosted and published"
order: 32
---

Local packages are built and described in [Packages and projects](../tour/packages): the manifest, path
dependencies, `export module`, and the packages the compiler ships ([Hosts](../advanced/hosts#packages-the-compiler-ships)).
This page frames the rest of the packaging workstream, which is a proposal: how packages are named, resolved, hosted
and published. Work is tracked under the `packaging` label.

# Summary

Nanyx should launch with a working package system: a program can depend on a published package by name and
version, `nanyx` fetches and builds it reproducibly, and anyone can publish one. Ecosystems that bolt this on
later (Python, early Go, C and C++) never fully recover from the years without it, so it is designed now, before
the first release, and built alongside the compiler.

The [Packages and projects](../tour/packages) page already describes the user's side:
`.nyxp` and `.nyxproj` manifests, imports of the form `acme.web/http`, `export module`, project
references and script mode (projects are planned). This page is about what that page leaves out: who owns a package name, how versions
are chosen, what a published package is, and where it lives.

# Scope

| Area | Where |
|------|-------|
| Manifests | Reading `.nyxp` and `.nyxproj`, checked against the docs |
| Module resolution | Imports by package and module name, `export module`, direct-dependency-only imports |
| Version resolution | Choosing one version of each package, the lockfile, error messages when no choice works |
| Package cache | Downloading, verifying and storing packages on the user's machine |
| CLI | `nanyx new`, `migrate`, `install`, `pull`, `build`, `run` (without a file), `publish`, `yank` |
| Package index | The service or repository that maps a name and version to an artifact |
| Publishing | Checks a package must pass before it is accepted, including the API compatibility check |
| Editor | The language server and extension resolving packages the way the CLI does |

Out of scope here:

- Distributing the compiler and the VS Code extension themselves (GitHub releases, Scoop, the Marketplace).
- The standard library's contents: the [`stdlib`](https://github.com/nanyx-lang/nanyx/labels/stdlib) issues. How it is
  *packaged* is in scope.
- Hosts and the contexts they provide: [Hosts](./hosts). Packaging has to
  carry a package's `host` field, but what a host is belongs to that design.

# Where the compiler is today

Built, and described in the user docs: local packages, path dependencies, public modules and the packages the
compiler ships ([Packages and projects](../tour/packages),
[Packages the compiler ships](../advanced/hosts#packages-the-compiler-ships)); the manifest
([The package manifest](./package-manifest)); GitHub references; the lockfile, the `.nyxpkg` format and the package
cache, read by builds and filled by `nanyx pull`; `api.json` and `nanyx bump`
([Versions](../tour/packages#versions)); and `nanyx new`, `migrate`, `install`, `pull`, `pack`, `bump` and `manifest`
([Packages](../standard-tools/command-line#packages)); choosing versions with PubGrub, writing the lockfile, and
`nanyx build --locked` ([#864](https://github.com/nanyx-lang/nanyx/issues/864)); and `nanyx publish`, `yank` and
`api-check` ([#865](https://github.com/nanyx-lang/nanyx/issues/865)). A program compiles to one WebAssembly module: imported modules
are checked separately and their items inlined, so there is no compiled artifact per module or package.

Not built: the index's check of a release's API (in nanyx-lang/packages, part of #865); projects (`.nyxproj`,
[#1386](https://github.com/nanyx-lang/nanyx/issues/1386)); `nanyx manifest` beyond `--json`; and the editor's side. How
the package commands are built is on [The nanyx command](../deep-dives/command-line#packages).
# Goals

- `nanyx install acme.json` followed by `nanyx run` works on a clean machine, and gives the same result on every
  machine that has the same lockfile.
- Publishing a version takes one command and needs no approval from a person. Only registering a package's name
  is reviewed, once.
- A published version never changes. It can be yanked, which stops new resolutions picking it, but builds that
  already lock it keep working. At launch the index doesn't mirror artifacts, so a version whose author deletes its
  release does disappear ([Where packages live](#where-packages-live)).
- A minor or patch release cannot break a dependant's build. The publish step checks this from the package's
  exported types, as Elm does, rather than trusting the author's version number.
- A dependency can only do what the contexts it requires allow, and the program's host decides which contexts
  exist. Packaging keeps that guarantee intact: no package gets `HostRuntime` unless it is the chosen host.
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

- Versions are semantic versions. In a manifest, `(package = "acme.core", version = "2.1")` means "2.1 or later, before 3.0", the
  caret range used by Cargo; an exact or wider range can be written explicitly.
- A build has **one version of each package**, as Elm does (decided, [#860](https://github.com/nanyx-lang/nanyx/issues/860)).
  A program is compiled into a single WebAssembly module and types
  from a dependency must unify wherever they are used, so two copies of `acme.core` would mean two incompatible
  sets of types. A conflict is a resolution error that names which packages want which versions. If a program truly
  needs two major versions, the new major is published under a new name
  (`acme.core2`), which keeps the rule simple and the cost visible.
- The resolver picks the newest version that satisfies every constraint (decided,
  [#859](https://github.com/nanyx-lang/nanyx/issues/859)), using
  [PubGrub](https://github.com/dart-lang/pub/blob/master/doc/solver.md) (as Dart and uv do). Its main attraction is that when
  there is no solution it explains why in plain sentences, which matches the error-message principles.
- `nanyx.lock` records the exact version and SHA-256 of every package in the build. It is written by `pull`,
  `install`, `build` and `run` when the manifest asks for something it doesn't lock
  ([#864](https://github.com/nanyx-lang/nanyx/issues/864)), is committed, and is the only input a build needs from
  the network. `nanyx build --locked` refuses to change it.
- Only direct dependencies can be imported, as the docs say. The resolver still sees the whole graph.

## Enforced semantic versioning

Decided, Elm-style ([#1470](https://github.com/nanyx-lang/nanyx/issues/1470), part of #865's publishing). Publishing
compares the new version's public API, written to `api.json`, with that of the newest published version below it. The
rules, and `api.json` and `nanyx bump`, which are built (`PackageApi.fs`), are described in
[Versions](../tour/packages#versions). `nanyx publish` refuses a version whose bump is too small, listing the changes
that need a bigger one, and the index refuses a release that skipped the check.

`nanyx api-check` runs the comparison on two `.nyxpkg` files, for the index's sync, which is to download the latest
compiler release and run it on each new release against the newest version recorded below it, reporting a release that
fails rather than recording it.

`nanyx publish` runs the same check against the newest version published below the one it publishes
(`PackageApi.publishCheck`). **Left for #865:** the index's sync running `nanyx api-check`.
## What a package is

A published package is a `.nyxpkg` file, built ([#863](https://github.com/nanyx-lang/nanyx/issues/863)): a zip of the
manifest, at its root, the source files its `sources` select, and `api.json` (`PackageArchive.pack`, `nanyx pack`). The
archive is the same from any checkout of the same commit: each file is written as UTF-8 with LF line endings and no
byte-order mark, and the entries' order, times, permissions and platform are fixed
([#2205](https://github.com/nanyx-lang/nanyx/issues/2205)), so anyone can rebuild a release's `.nyxpkg` from its tag
and check it against the index's SHA-256.

- Packages ship **source**. The compiler inlines imported modules into one WebAssembly module, so there is no
  compiled form to ship yet, and source works for every compiler version that supports the package's language
  level. A cached compiled form can be added later, as an optimisation the cache owns.
- **Not built:** the readme and licence in the archive, and the manifest's `package` gaining `nanyx = "0.14"` (the
  minimum compiler version), `license` and `description`. Its `repository` is built: `nanyx publish` needs it.
- `api.json` is what the API comparison and the package website read, so neither has to compile the package.
## Where packages live

The client speaks one small protocol: **a static index served over HTTPS**, like Cargo's sparse index. For each
package there is one JSON file listing its versions, each with the artifact URL, its SHA-256, its dependencies,
its minimum compiler version and whether it is yanked. Because the protocol is just files, what serves them can
change without the client noticing.

**The index exists** ([#209](https://github.com/nanyx-lang/nanyx/issues/209)). It is the repository
[`nanyx-lang/packages`](https://github.com/nanyx-lang/packages), served by GitHub Pages at
`https://nanyx-lang.github.io/packages/`, in the style of Julia's General registry, Homebrew and our own Scoop
bucket. Its README is the format's specification; in short:

- **`config.json`** gives the format version (1) and the address of the index files. A client reads it first and
  builds every other address from it, so moving to `packages.nanyx.dev` later is a change there and a redirect,
  not a client release.
- **One file per package**, `index/<namespace>/<name>.json`, lists every version with its `url`, `sha256`,
  `nanyx` (minimum compiler), `dependencies`, `yanked` and `available`.
- **The index stores no artifacts and mirrors nothing.** A package's versions are the releases of its own GitHub
  repository: a release tagged `v1.2.0` or `1.2.0` with an asset named `<name>-<version>.nyxpkg`. The client
  also unpacks a zip of a package's directory, with its manifest in its one top-level folder, which the index accepts
  for now.
- **Registering a package** is a pull request adding `registry/<namespace>/<name>.json`, which holds only the name
  and the repository. A check refuses, saying why, a malformed name, one that is taken or too like a registered
  one, and a repository that is missing, private, or not owned by the namespace's owner (`nanyx` belongs to
  `nanyx-lang`). A maintainer reviews and merges registrations by hand.
- **A sync workflow** runs every hour, on every push and by hand. It reads each registered repository's releases,
  downloads each new version's asset, records its SHA-256 and its manifest's fields, commits the index changes
  with the workflow's own token, and publishes the site. A version's hash is recorded once: a replaced asset is
  reported and the old hash kept, so clients refuse the new asset. A version whose release disappears is marked
  `available: false` and reported, never removed.
- **Yanking** is a pull request that sets `yanked` on a version; the check refuses any other change to an index
  file.

The workflows' scripts and their tests are in the index repository. The owner check compares the namespace with
the repository owner's current login; recording the owner's account ID, as
[Package ownership and locking](./package-ownership-and-locking#the-claim-is-kept-by-account-id) proposes, is
still to do.

What this gives up: because nothing is mirrored, an author who deletes a release or a repository breaks builds that
need it. The hash protects integrity, not availability. Mirroring can be added later behind the same `url` field,
with no client change.

**Publishing is built** ([#865](https://github.com/nanyx-lang/nanyx/issues/865)): `nanyx publish` builds the asset,
creates the release on the package's own repository, and opens the registration pull request for a package's first
version, and `nanyx yank` opens the pull request that yanks a version
([`nanyx publish`](../standard-tools/command-line#nanyx-publish)). When pull requests become the bottleneck, a small service can take over registration and syncing behind an upload API with tokens, and keep
writing the same static index. The client, the lockfile and every existing package are unaffected.

Alternatives considered:

- **A registry service from day one** (crates.io, npm). It needs accounts, tokens, moderation tooling, hosting and
  someone on call, before there is an ecosystem to justify it. The static index gets the same client experience
  now and leaves this as an upgrade.
- **Dependencies as git URLs** (Go modules, early Swift). There is no index to run, but names are URLs, which
  contradicts the documented `(package = "acme.core", version = "2.1")`; tags can be moved, so it needs a checksum database anyway; and
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

Built ([#863](https://github.com/nanyx-lang/nanyx/issues/863)): downloaded packages are verified against the
lockfile's SHA-256 and unpacked into a per-user cache (`~/.nanyx/packages`, or `NANYX_PACKAGES`), shared by every
project and never modified, by `nanyx pull` only (`PackageIndex.download`); a build only reads the cache. The index is
`NANYX_INDEX`, if it is set: an address, or a directory on disk, which is how the tests use it. A yanked version still
downloads, since a lockfile that names it must keep building.

**Not built:** `nanyx build --offline`, which stops a build that would update the lockfile from reaching the index.

## The editor

The language server resolves imports exactly as the CLI does, from the manifest, lockfile and cache. When the
manifest changes, the extension offers to run `nanyx pull`. Go-to-definition into a dependency opens the
cached source read-only.

# Plan

Done: imports by module name within a package ([#287](https://github.com/nanyx-lang/nanyx/issues/287)), the rest of the
import syntax ([#289](https://github.com/nanyx-lang/nanyx/issues/289)), path dependencies
([#862](https://github.com/nanyx-lang/nanyx/issues/862)), packages shipped with the compiler
([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)), resolution and writing the lockfile
([#864](https://github.com/nanyx-lang/nanyx/issues/864)), the console host as one
([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)), GitHub references
([#1472](https://github.com/nanyx-lang/nanyx/issues/1472), [#1742](https://github.com/nanyx-lang/nanyx/issues/1742)),
the lockfile and the cache ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), the index
([#209](https://github.com/nanyx-lang/nanyx/issues/209)), `export module`
([#1457](https://github.com/nanyx-lang/nanyx/issues/1457)), and `api.json`, the API comparison and `nanyx bump`
(#1470), and a script's dependencies on packages from the index, each pinned at the exact version its `::dependency`
writes ([#867](https://github.com/nanyx-lang/nanyx/issues/867), see
[Package ownership and locking](./package-ownership-and-locking#scripts)). Local packages came first, so that hosts and linters could become packages
([Built-ins as packages](./built-ins-as-packages)) without waiting for downloads.

1. **The official linter as a shipped package**, once its contract exists
   ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)).
2. **Publishing.** `nanyx publish` and `yank` are built; the index's check of a release's API, in its sync
   ([#865](https://github.com/nanyx-lang/nanyx/issues/865)), is left.
3. **The index:** recording owners' account IDs, and mirroring artifacts.
4. **Projects** (`.nyxproj`, project references) when a workspace of several packages needs them
   ([#1386](https://github.com/nanyx-lang/nanyx/issues/1386)).
5. **Editor support.** Resolve packages in the language server; pull from the extension.
6. **First packages.** Publish at least one official package end to end before launch, to prove the whole path.
   `nanyx.json` is the trial ([#867](https://github.com/nanyx-lang/nanyx/issues/867)): it is no longer shipped, and
   lives in [nanyx-lang/json](https://github.com/nanyx-lang/json), with its tests and a CI workflow that builds them
   with the latest released compiler. What the trial turned up so far:
   - A script couldn't depend on a package from the index, so no script, the docs' examples among them, could use a
     package that isn't shipped. A script now pins the exact version it uses (#2179).
   - The docs site's reference pages are generated from the standard library and the shipped packages only, so
     `json`, `json, decoders` and `encode` keep their guides but link to the doc comments in nanyx-lang/json for the
     reference ([#2178](https://github.com/nanyx-lang/nanyx/issues/2178)).
   - A script in a package's directory is one of the package's files, and a package has one `@entry`, so a package's
     tests can't be scripts beside it. nanyx-lang/json's test runner copies the package and its test scripts to a
     directory of their own and compares what each prints with an `.expected` file, until `nanyx test`
     ([Testing](./testing)) replaces it.
   - `nanyx publish` creates the GitHub release before it opens the registration pull request on the index, and a
     second run refuses a version whose release exists. If the pull request can't be opened, for example because
     the token can't reach nanyx-lang/packages, the publish can't be finished by running it again.
   - The compiler's tests need the package without the network: they read the `.nyxpkg` of its release, kept beside
     them, from an index on disk.
   - `nanyx pack` packs the files as they are on disk, so the same version packed on two machines can differ: 0.1.0,
     published from a Windows checkout, holds its sources with CRLF line endings, and its SHA-256 isn't that of the
     same commit packed on Linux. Packing now writes LF line endings whatever the checkout has (#2205).
   - `nanyx publish` 0.31.0 couldn't publish a package's first version: the API comparison read the 404 for a package
     the index doesn't list as an error ([#2184](https://github.com/nanyx-lang/nanyx/pull/2184)). Pointing
     `NANYX_INDEX` at an empty index on disk got past it.
   - The index's Sync refused the release: the manifest writes its `repository` as `owner/name`, which `publish`
     needs, and the registration `publish` opens gives its address, which Sync compared literally
     ([nanyx-lang/packages#6](https://github.com/nanyx-lang/packages/pull/6)).
   - `nanyx publish` opens the registration pull request from a fork of nanyx-lang/packages in the publisher's
     account, which it makes if need be, even for an account that can push to the index itself.
# Open questions

- **A custom domain and mirroring.** The index is at `https://nanyx-lang.github.io/packages/` and points at
  release assets on packages' own repositories ([#209](https://github.com/nanyx-lang/nanyx/issues/209)). When
  should it move to `packages.nanyx.dev`, and who holds the domain? Should artifacts be mirrored (to object storage
  or a project-owned repository) so a deleted release can't break builds?
- **Hosts and capabilities.** Should a package declare the contexts it requires in its manifest, so the index can
  show "this package needs `FileSystem`" before anyone installs it?
- **Private packages.** Should the client support more than one index (a company's own) from the start? The
  static protocol makes it cheap: a list of index URLs in user configuration.
