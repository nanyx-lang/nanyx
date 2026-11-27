---
title: "The package index"
description: "How published packages are hosted, registered, synced, locked and cached"
order: 11.5
---

How to depend on a package from the index, and how to publish one, is described for users in
[Packages and projects](../tour/packages#package-dependencies) and under
[Packages](../standard-tools/command-line#packages) on The nanyx command. How the CLI's package commands are built,
and how versions are chosen, is on [The nanyx command](./command-line#packages). This page says how the index, the
lockfile and the package cache work. What is still planned is on [Packaging](../work-in-progress/packaging) in Work in
progress.

# The index

The index is the repository [`nanyx-lang/packages`](https://github.com/nanyx-lang/packages), served by GitHub Pages
at `https://packages.nanyx.dev/`. Its README is the format's specification. A client speaks one small
protocol, a static index read over HTTPS, as Cargo's sparse index is, so what serves the files can change without the
client noticing. `PackageIndex.fs` reads it.

- **`config.json`** gives the format version, `format` (1), and where the index files are, `index`. A client reads it
  first and builds every other address from it, so the index can move to another address with a change there and a
  redirect, not a client release. The compiler refuses a format it doesn't read.
- **One file per package**, `<index><namespace>/<name>.json`, lists every version ever recorded with its `url`,
  `sha256`, `nanyx` (the compilers it builds with, as its manifest writes them: `0.50`, that one or later, or `=0.50.0`, that one
  only), `dependencies`, `unlisted` and `available`
  (`PackageIndex.versions`). An older file writes `unlisted` as `yanked`, which the compiler reads as the same. A
  package the index doesn't list has no file: a 404, which is read as no versions.
- **The index stores no artifacts and mirrors nothing.** A package's versions are the releases of its own GitHub
  repository: a release tagged `v1.2.0` or `1.2.0` with an asset named `<name>-<version>.nyxpkg`. The client also
  unpacks a zip of a package's directory, with its manifest in its one top-level folder (`PackageArchive.unpack`).

`NANYX_INDEX` names another index: an address, or a directory on disk laid out the same way, which is how the tests use
one (`PackageStore.user`).

## Registering a package

Registering a package is a pull request adding `registry/<namespace>/<name>.json`, which holds only the name and the
repository. The index's Check workflow refuses, saying why, a malformed name or one with a single part, a name that is
taken or differs from a registered one only in `_` or in characters that look alike, and a repository that is missing,
private, or not owned by the namespace's owner. A namespace belongs to the GitHub user or organisation of the same
name, in lower case, compared with the repository owner's current login; `nanyx` belongs to `nanyx-lang`. The index's
scripts and their tests are in its repository. A maintainer
reviews and merges registrations by hand. `nanyx publish` opens this pull request for a package's first version.

## Syncing releases

The Sync workflow runs every hour, on every push and by hand. For each registered package it reads the repository's
releases, downloads each new version's asset, and reads its manifest with the latest compiler release
(`nanyx manifest --json`). It refuses to record, and reports, a release whose manifest's name or version doesn't match
it, whose `repository` isn't the one registered, or which has a dependency found by its path or on GitHub. It commits
the index changes with the workflow's own token and publishes the site.

A version's hash is recorded once: a replaced asset is reported and the old hash kept, so clients refuse the new
asset. A version whose release disappears is marked `available: false` and reported, never removed.

Before it records a version, Sync runs `nanyx api-check` on it and the newest version recorded below it that isn't
unlisted and is available, and a version whose number is too small for its API's changes is reported, not recorded. A
package's first version isn't checked.

A release's tag is `v1.2.0` or `1.2.0`, perhaps after a prefix that ends in `/`. The compiler's releases,
`compiler/0.50.0`, carry the [packages it ships](./shipped-packages), each registered with `nanyx-lang/nanyx`, so Sync
records them as it does any package ([Releases](./releases#the-shipped-packages)). The API check holds them to their
version too: a compiler release whose shipped package breaks its API in a patch release, or in a minor one from 1.0.0
up, records nothing for that package, and Sync reports it.

## Unlisting

Unlisting is a pull request that sets `unlisted` on a version, which `nanyx unlist` opens (`Publishing.unlist`). The
Check workflow refuses any other change to an index file. An unlisted version is ruled out of new resolutions, but still
downloads, since a lockfile that locks it, or a script that pins it, keeps building. A command that reads its entry in
the index warns that it is unlisted (`NYX0051`).

# The lockfile

`nanyx.lock` is a Nanyx record, as the manifest is, written and read by `Lockfile.fs`:

```text
-- Written by nanyx, which keeps it up to date: don't edit it by hand.
(
  version = 1
  packages = [
    (package = "acme.core", version = "2.1.3", sha256 = "9f2c41…", dependencies = [])
  ]
)
```

`version` is the format's version. Each entry is a package from the index: its exact version, the SHA-256 of its
`.nyxpkg`, and the names of the packages it depends on, each locked too. Entries are sorted by name, one to a line, so
two branches that lock different packages rarely conflict. Only packages from the index are locked, the packages the
compiler ships among them: a path dependency's source changes with the code around it, and a dependency on GitHub is
pinned by its commit in the manifest. A package the lockfile doesn't lock yet, whose requirement the version the
compiler seeded accepts, is read from that copy until the next resolution locks it, which reports it with no line of
its own, so a package needs no `nanyx pull` to build under the host the compiler ships. A package's dependencies named by version are resolved and locked with the
package that reads the lockfile; a dependency's own lockfile isn't read.

# The cache

`nanyx pull` downloads each locked package into the cache, `~/.nanyx/packages` or `NANYX_PACKAGES`, at
`<name>/<version>`, shared by every project and never modified (`PackageIndex.download`). It checks the download's
SHA-256 against the lockfile's before it unpacks it, and that what it unpacked is the package and version it was
downloaded as. A build only reads the cache. A package in the cache keeps its checked modules beside it, so a build
doesn't check it again ([The nanyx command](./command-line#packages)).

A `.nyxpkg` packs to the same bytes from any checkout of the same commit (`PackageArchive.fs`), so anyone can rebuild a
release's asset from its tag and check it against the index's SHA-256. Its entries are stored, not compressed: deflate's
output differs between builds of .NET and of zlib, so a compressed package packed by the compiler's release and by the
same compiler built from source would differ. A package is a few kilobytes of source, so this costs little. Packages
packed by 0.44.0 or later are the first that can be rebuilt byte for byte. Versions packed before it were compressed:
they can't be rebuilt to the same bytes, and keep the SHA-256 the index recorded for them.

# Why it's built this way

- **Static files rather than a registry service.** A registry from day one, as crates.io and npm have, needs accounts,
  tokens, moderation tooling, hosting and someone on call before there is an ecosystem to justify it. The static index
  gives the same client experience, costs close to nothing, and can be written by a service later without changing the
  client or the lockfile.
- **Not git URLs as names,** as Go modules and early Swift have. There would be no index to run, but names would be
  URLs, tags can be moved, so a checksum database is needed anyway, and every build would depend on the source host
  staying up.
- **Not an OCI registry,** such as GitHub Container Registry. It is content-addressed and free for public packages, but
  names would be tied to the registry's paths and every client would need an OCI client. It remains an option for
  storing artifacts behind the index's own URLs.
- **Not an existing package manager,** such as npm or NuGet. Its naming, versioning and tooling would leak into Nanyx,
  and the API check could not run when a version is published.
- **Nothing is mirrored, for now.** The hash protects integrity, not availability: an author who deletes a release or
  a repository breaks builds that need it. Mirroring can be added behind the same `url` field with no client change.
- **Namespaces are GitHub accounts.** They reuse an identity people already have and give verified ownership with no
  account system of the project's own. Every package has at least two segments, so a bare name like `json` can't be
  claimed by whoever comes first. The `nanyx` namespace holds the project's own packages, including those, such as
  `nanyx.json`, that are published from repositories of their own rather than shipped with the compiler. The
  alternatives were names chosen on the registry, as scopes are on JSR and npm, where someone has to allocate names
  and settle disputes; flat names, as on crates.io and PyPI, where the good names go to whoever comes first; and the
  repository's URL as the name, as in Go, where moving the code renames the package. Keeping a claim through a GitHub
  rename is proposed on [Package ownership and locking](../work-in-progress/package-ownership-and-locking).
- **One version of each package in a build,** as Elm has. A program is one WebAssembly module, and types from a
  dependency must unify wherever they are used, so two copies of `acme.core` would be two incompatible sets of types. A
  program that needs two major versions takes the new major under a new name (`acme.core2`), which keeps the rule
  simple and its cost visible.
- **PubGrub,** as Dart and uv use, because when there is no solution it explains why in plain sentences.
- **The version is checked, not trusted.** A minor or patch release can't break a dependant's build, because
  `nanyx publish` and the index's sync compare the exported API, as Elm does, rather than trusting the author's
  version number. `api.json` is packed with each version so that neither the comparison nor the index's site has to
  compile the package.
- **Packages ship source.** The compiler inlines imported modules into one WebAssembly module, so there is no compiled
  form to ship, and source works with every compiler that supports the package's language level.
