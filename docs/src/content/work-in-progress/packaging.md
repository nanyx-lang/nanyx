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
references and script mode. This page is about what that page leaves out: who owns a package name, how versions
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

Local packages work: a package and the packages it depends on by path are built, checked and run together. Projects
(`.nyxproj`) and everything that needs a downloaded package are only in the docs.

- **A package can be built and run** (`Package.fs`). The manifest is one Nanyx record written as data, read by
  `Manifest.check` ([The package manifest](./package-manifest)). Its `package` (`name`, `version`, `sources`,
  `host`), `dependencies`, `linters` and `host` are read, with an error at the field for anything wrong or
  unknown (`NYX0020` to `NYX0024`), a repeated `dependencies` entry among them. A library (no `host`, no
  `@entry`) whose files export no module is `NYX0033` at its `package`, when the package is read for a build
  (`Package.exportErrors`) and in the editor. `nanyx build`, `nanyx check` and `nanyx run` without a file (or with a
  directory or manifest) check every source file, each module once however many files import it (`Package.build`,
  [#698](https://github.com/nanyx-lang/nanyx/issues/698)), and `run` runs the one `@entry`; `build` writes it to
  `out/<name>.wasm`. `nanyx new` makes a package (`NewPackage.fs`). The language server reads the manifest's linter
  configuration too. Path dependencies work (`Package.dependenciesIn`): each is read with its own, and a directory without a manifest, a
  name that doesn't match, a cycle (`NYX0027`) or a package reached by two paths (`NYX0028`) is an error; a
  dependency's `version` is the version `nanyx.lock` locks, unpacked in the cache, which `nanyx pull` downloads into ([#863](https://github.com/nanyx-lang/nanyx/issues/863)); one the lockfile doesn't lock is `NYX0024` until versions can be chosen ([#864](https://github.com/nanyx-lang/nanyx/issues/864)). There is no `.nyxproj`. `nanyx migrate` makes a package of scripts, `nanyx pull` downloads and lists its dependencies, `nanyx install` adds one, and `nanyx pack` packs it; for a package named by its version that the lockfile doesn't lock, `pull` and `install` are stubs ([#1471](https://github.com/nanyx-lang/nanyx/issues/1471), [The nanyx command](./command-line#packages)). The formatter
  reads the manifest's `formatter` field itself.
- **The compiler ships packages as source directories** beside it (`ShippedPackages`): the console host,
  `packages/nanyx.console`, and its interface package, `packages/nanyx.console.core`, at the compiler's version, which
  a manifest's `host` or `dependencies` names at that version exactly (`NYX0029`) (see
  [Built-ins as packages](./built-ins-as-packages#how-shipped-packages-are-built)).
- **A package's imports go through its module table** (`PackageModules.fs`). Every source file the manifest
  selects is keyed by the module name it declares (`PackageModules.ofFiles`), and in those files `import greetings`
  and `import acme.web/greetings` both reach the file that declares `module greetings`, wherever it is
  (`PackageModules.resolveImport`). An unknown module is `NYX0003`, naming the package, the file named for the module
  if it declares another name, or close names. A source file must declare a module name and have no top-level
  statements (`NYX0017`). Files that declare the same module are its fragments, each marked `@partial`, and are
  assembled into one module before it is checked (`PartialModules.assemble`); without `@partial` on each, it is
  `NYX0018`. `PackageModules` holds the packages it depends on too: `import acme.util/strings` reaches a module a direct
  dependency declares with `export module` (`NYX0025` for one it doesn't, `NYX0026` for a package only a dependency
  depends on), and a dependency's own files resolve within that package (`PackageModules.ownerOf`). Dependencies'
  modules are checked once per build, into the same program. `Package.build`, a single file of a package compiled
  with `nanyx run`/`check`, and the language server (`Package.compileSourceWith`) all resolve this way; the language
  server checks a `.nyxp` with its dependencies (`Package.compileManifest`).
- **Outside a package, imports resolve two ways only** (`FileImports.resolvePath` and
  `StandardLibrary.tryModuleName`). A module import `a.b/c` becomes the file `a/b/c.nyx` next to the importing file,
  with `.` and `/` treated alike. A name starting `nanyx/` comes from the standard library embedded in the compiler.
  There are no search paths, and a module's declared name is not checked against the name it was imported by. An
  import by file path, in a package or not, must have an alias (`NYX0014`) and must name a script module, not one
  that declares `module name` (`NYX0015`), and no import may name an entrypoint, a file with top-level statements
  (`NYX0016`), as the [modules page](../tour/modules#importing-modules-by-file-path) says (`FileImports.fs`).
- **The documented import syntax works.** `import x as m`, grouped imports, selections (`as (a, b)`,
  `as m & (a, b)`) and re-exports (`export collections.list (map, filter)`). A selection brings in only the exports
  it names, and the rest are inlined as private definitions are (`selectedExports`, `inlinedRenames`). A module
  keeps its re-exports (`CheckedModule.ReExports`), and an importer merges each as a made-up import of the module
  re-exported from, under its own alias (`mergeWithReExports`), so the names keep their original symbols.
- **A program compiles to one WebAssembly module.** Imported modules are type-checked separately and their items
  inlined into the root (`inlinedItemsForImport`), so there is no linking step and no compiled artifact per
  module or package.
- **The standard library is versioned with the compiler.** It is embedded as resources and is described in
  [Standard library implementation](./standard-library-implementation) as "an ordinary package named `nanyx`", which
  is the model this page keeps.

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
  `add` and `build`, is committed, and is the only input a build needs from the network.
- Only direct dependencies can be imported, as the docs say. The resolver still sees the whole graph.

## Enforced semantic versioning

Decided, Elm-style ([#1470](https://github.com/nanyx-lang/nanyx/issues/1470), part of #865's publishing). Publishing
compares the new version's public API, written to `api.json`, with that of the newest published version below it:
the exported definitions of every exported module, with their types.

- **Major:** an export removed or renamed; a def's type changed other than made more general; a new context
  requirement on an exported def; a field removed from an exported record type; a tag added to or removed from an
  exported closed union (adding one breaks exhaustive matches, as in Elm); a type's parameters changed; an exported
  type made protected or private.
- **Minor:** new exports or modules, a type made more general, a requirement dropped.
- **Patch:** no change to the public API.
- **Before 1.0.0** a breaking change needs a minor bump (`0.3` to `0.4`), as in Cargo.

`nanyx publish` refuses a version whose bump is too small, listing the changes that need a bigger one, and
`nanyx bump` prints the changes and writes the right version into the manifest. The index refuses a release that
skipped the check.

**Built** ([#1470](https://github.com/nanyx-lang/nanyx/issues/1470)), but for what waits on publishing (#865), in
`PackageApi.fs`:

- **`api.json`** (`PackageApi.ofPackage`, `toJson`, `ofJson`): for each module exported with `export module`, sorted,
  each export with its kind (`def`, `type`, `context`) and what another package sees of it: a def's type, after the
  contexts it requires, as hover writes it, or a type's whole definition. `nanyx pack` puts it in the `.nyxpkg`, so the
  baseline's API is read from the cache, not compiled again.
- **The comparison** (`PackageApi.compare`): the rows above, each a change with the bump it needs and a sentence that
  says what it is. Types are compared as written: both are parsed as annotations, and a new def type is more general
  when the old one is it with its type variables replaced; a type variable renamed is no change. Contexts are compared
  after that substitution, so a context added is major and one dropped minor. Any change to an exported type's
  definition is major, described as a field or tag removed or added, its parameters changed, or it made protected or
  private, where that can be told. A field *added* to a record type is major too, since a record written without it is
  no longer one; the decision didn't list it.
- **The version rules** (`required`, `bumped`, `allows`), with the 0.x rule, and **`versionErrors`**, what
  `nanyx publish` refuses: a version not after the newest published, or a bump too small, with the changes that need
  the bigger one.
- **`nanyx bump`** reads the newest version of the package published at or below the manifest's from the index,
  downloads it into the cache, and writes into the manifest that version bumped as the changes need, unless the
  manifest already says a later version that is big enough. With nothing published, the version stays.

**Left for #865:** `nanyx publish` calling `versionErrors` against the newest version published below the one it
publishes, and the index's check that a release's `api.json` came from a bump that passed it.

This needs the type of every exported definition, which inference already has, and it is one more reason for the
linter rule that asks for annotations on exported definitions
([#104](https://github.com/nanyx-lang/nanyx/issues/104)): an inferred type can change by accident.

## What a package exports

Built ([#1457](https://github.com/nanyx-lang/nanyx/issues/1457)), and described in
[Public modules](../tour/packages#public-modules): `export module` in a module's declaration makes it public, and
re-exports choose what of an internal module it shows. The compiler reads it into `PackageModules.PublicModules`
(`ModuleDecl`'s `isExport`, by `PackageModules.ofFiles`; a module split across files is public when one of them
exports it). These public modules' exports, with their types, are the API that `api.json` and the
[semantic version check](#enforced-semantic-versioning) compare.

## What a package is

A published package is a `.nyxpkg` file: a gzipped tar holding the `.nyxp` manifest, the source files the
manifest selects, the readme and licence, and a generated `api.json` describing the public API.

- Packages ship **source**. The compiler inlines imported modules into one WebAssembly module, so there is no
  compiled form to ship yet, and source works for every compiler version that supports the package's language
  level. A cached compiled form can be added later, as an optimisation the cache owns.
- The manifest's `package` gains `nanyx = "0.14"` (the minimum compiler version) and `license`, `description` and
  `repository`, which the index requires.
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
  repository: a release tagged `v1.2.0` or `1.2.0` with an asset named `<name>-<version>.nyxpkg`. Until the
  `.nyxpkg` format lands ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), the asset is a zip of the
  package's sources, with its manifest at the root.
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

**Next**, `nanyx publish` ([plan item 7](#plan)) will build the asset, create the release on the package's own
repository, and open the registration pull request for a package's first version. When pull requests become the
bottleneck, a small service can take over registration and syncing behind an upload API with tokens, and keep
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

Downloaded packages are verified against the lockfile and unpacked into a per-user cache
(`~/.nanyx/packages/<name>/<version>/`, or `NANYX_PACKAGES`), shared by every project. A package in the cache is never
modified. `nanyx build --offline` uses only the cache, and a build with a complete lockfile and a warm cache never touches
the network.

Built ([#863](https://github.com/nanyx-lang/nanyx/issues/863)), but for `--offline`, which waits for resolution (#864):
a build only ever reads the cache, and only `nanyx pull` downloads. `PackageIndex.download` reads the index's
`config.json` and the package's index file, downloads the version's `.nyxpkg` (a path relative to the index file, or
an address), refuses it unless its SHA-256 is the lockfile's, unpacks it beside its directory in the cache and moves it
there, so a package is never seen half unpacked, and checks that its manifest names the package and version it was
downloaded as. A yanked version still downloads, since a lockfile that names it must keep building. The index is
`NANYX_INDEX`, if it is set: an address, or a directory on disk, which is how the tests use it.

A `.nyxpkg` is a zip of the manifest, at its root, and the files its `sources` select, at their paths beside it, in
order of their paths and all with the same time, so packing the same package twice makes the same bytes
(`PackageArchive.pack`). One with a path dependency, or one on GitHub, can't be packed. Unpacking also takes the zip of
a package's directory, with the manifest in its one top-level folder, which the index accepts for now, and refuses an
entry that would land outside the package's directory.

## The editor

The language server resolves imports exactly as the CLI does, from the manifest, lockfile and cache. When the
manifest changes, the extension offers to run `nanyx pull`. Go-to-definition into a dependency opens the
cached source read-only.

## Local packages first

Decided by the maintainer. Packaging is built in two halves, so that hosts and linters can become packages
([Built-ins as packages](./built-ins-as-packages)) without waiting for the parts that only downloaded packages need:

- **Local packages, now.** A dependency is a package in a directory on disk, a path dependency
  (`(package = "acme.util", path = "../util")` in `dependencies`), or a package shipped with the compiler. Nothing is
  downloaded, so there is nothing to choose between and no solver is needed.
  - **Shipped packages** (the console host, then the official linter) are source directories in the compiler's
    distribution, each with its `.nyxp` and sources, compiled like a path dependency. They become `.nyxpkg` once the
    cache exists.
  - **Versions.** A manifest names a shipped package with the version the compiler ships, exactly. Anything else is
    an error saying which version is available, and `nanyx new` writes the right one.
  - **Projects** (`.nyxproj`, project references) wait until a workspace of several packages needs them
    ([#1386](https://github.com/nanyx-lang/nanyx/issues/1386)).
- **The manifest is Nanyx** ([The package manifest](./package-manifest)), so path dependencies are added to that form.
- **Remote packages, later:** `.nyxpkg`, the cache, `nanyx.lock`, resolving versions, `nanyx install` and `pull`,
  publishing and `api.json`. None of this page's open questions block the local half.

# Plan

Local packages first (items 3a to 3c), then remote packages. Each produces something usable on its own:

1. **Imports by module name within a package.** Done ([#287](https://github.com/nanyx-lang/nanyx/issues/287)).
2. **The rest of the import syntax.** Done ([#289](https://github.com/nanyx-lang/nanyx/issues/289)): selections and
   re-exports, which `export module` relies on.
3. **Local packages:**
   - ~~**3a. Path dependencies** ([#862](https://github.com/nanyx-lang/nanyx/issues/862)): a dependency on a package in
     a directory, importing its public modules as `acme.util/strings`, one version of each package per build, only
     direct dependencies importable, with an error for each rule.~~ Done.
   - ~~**3b. Packages shipped with the compiler** ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342)): source
     directories in the distribution, named with an exact version.~~ Done.
   - **3c. The console host as a shipped package** ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344), done), then the
     official linter once its contract exists ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)).
   - Projects (`.nyxproj`) later ([#1386](https://github.com/nanyx-lang/nanyx/issues/1386)).
   - **3d. GitHub references** ([#1472](https://github.com/nanyx-lang/nanyx/issues/1472), done but for the download): a
     package, or one file as a module, from a GitHub repository, pinned to a commit; see
     [Package ownership and locking](./package-ownership-and-locking#depending-on-a-single-file). Fetching waits for
     the cache (item 4).
4. ~~**The lockfile and the cache.** `nanyx.lock`, the `.nyxpkg` format, verification and the per-user cache, tested
   against a local directory acting as an index.~~ Done ([#863](https://github.com/nanyx-lang/nanyx/issues/863)):
   `Lockfile.fs`, `PackageArchive.fs` (`nanyx pack`), `PackageIndex.fs` and `PackageStore.fs`; see
   [The cache and offline builds](#the-cache-and-offline-builds). Nothing writes the lockfile until item 5.
5. **Resolution.** PubGrub over the index protocol, with error messages for conflicts; `nanyx install` and `pull`.
6. **The index.** Done ([#209](https://github.com/nanyx-lang/nanyx/issues/209)): `nanyx-lang/packages` on GitHub
   Pages, with the registration check, the release sync and the `nanyx` namespace reserved. Not done: recording
   owners' account IDs, and mirroring artifacts.
7. **Publishing.** `nanyx publish` and `yank`; `api.json`; the API comparison and version check
   ([#865](https://github.com/nanyx-lang/nanyx/issues/865), [#1470](https://github.com/nanyx-lang/nanyx/issues/1470)).
   `export module` ([#1457](https://github.com/nanyx-lang/nanyx/issues/1457), done) decides what the API is.
   `api.json`, the comparison and `nanyx bump` are done (#1470); `publish` and `yank` are left.
8. **Editor support.** Resolve packages in the language server; pull from the extension.
9. **First packages.** Publish at least one official package (`nanyx.json`, say) end to end before launch, to
   prove the whole path.

# Open questions

- **A custom domain and mirroring.** The index is at `https://nanyx-lang.github.io/packages/` and points at
  release assets on packages' own repositories ([#209](https://github.com/nanyx-lang/nanyx/issues/209)). When
  should it move to `packages.nanyx.dev`, and who holds the domain? Should artifacts be mirrored (to object storage
  or a project-owned repository) so a deleted release can't break builds?
- **Hosts and capabilities.** Should a package declare the contexts it requires in its manifest, so the index can
  show "this package needs `FileSystem`" before anyone installs it?
- **Private packages.** Should the client support more than one index (a company's own) from the start? The
  static protocol makes it cheap: a list of index URLs in user configuration.
