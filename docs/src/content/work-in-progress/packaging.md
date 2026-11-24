---
title: "Packaging"
description: "What is still planned and open for packages: the editor, projects, offline builds, mirroring and ownership"
order: 32
---

Packages, their dependencies, versions and publishing are described for users in
[Packages and projects](../tour/packages) and under [Packages](../standard-tools/command-line#packages) on The nanyx
command. How the package index, the lockfile and the cache work, and why they are built that way, is on
[The package index](../deep-dives/package-index), and how the package commands are built on
[The nanyx command](../deep-dives/command-line#packages). This page keeps what is still planned or undecided. Work is
tracked under the [`packaging`](https://github.com/nanyx-lang/nanyx/labels/packaging) label.

# Plan

1. **The official linter as a shipped package**, once its contract exists
   ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343), [Built-ins as packages](./built-ins-as-packages)).
2. **Ownership in the index.** Recording a namespace owner's GitHub account ID, so that a claim survives a rename,
   and the publishers an organisation grants ([Package ownership and locking](./package-ownership-and-locking)).
   The owner check compares a namespace with the repository owner's current login.
3. **Mirroring artifacts**, so that a deleted release can't break builds ([Open questions](#open-questions)).
4. **A service behind the index.** When pull requests become the bottleneck, a small service can take over
   registration and syncing behind an upload API with tokens, and keep writing the same static index. The client, the
   lockfile and every existing package are unaffected.
5. **Projects** (`.nyxproj`, project references) when a workspace of several packages needs them
   ([#1386](https://github.com/nanyx-lang/nanyx/issues/1386)).
6. **Editor support** ([#866](https://github.com/nanyx-lang/nanyx/issues/866)). The language server resolves imports
   exactly as the CLI does, from the manifest, lockfile and cache. When the manifest changes, the extension offers to
   run `nanyx pull`. Go-to-definition into a dependency opens the cached source read-only.
7. **The manifest's and the archive's remaining parts.** The manifest's `package` gains `license` and `description`;
   the `.nyxpkg` gains the package's readme and licence ([#2264](https://github.com/nanyx-lang/nanyx/issues/2264)).
8. **The CLI's remaining parts.** `nanyx update`, which upgrades a locked version on purpose
   ([#2265](https://github.com/nanyx-lang/nanyx/issues/2265)); `nanyx build --offline`, which stops a build that would
   update the lockfile from reaching the index ([#2266](https://github.com/nanyx-lang/nanyx/issues/2266)); `nanyx pull`
   checking what it downloads, so that the first build after it is fast too; and `nanyx manifest` in forms other than
   `--json`.

# Open problems

- **A package's tests can't be scripts beside it.** A script in a package's directory is one of the package's files,
  and a package has one `@entry`. [nanyx-lang/json](https://github.com/nanyx-lang/json)'s test runner copies the
  package and its test scripts to a directory of their own and compares what each prints with an `.expected` file.
  `nanyx test` ([Testing](./testing)) is to replace it, and `nanyx publish` is to run a package's tests with it.

# Open questions

- **Mirroring.** The index, at `https://packages.nanyx.dev/`, points at release assets on packages' own
  repositories. Should artifacts be mirrored (to object storage or a project-owned repository) so a deleted release
  can't break builds?
- **Hosts and capabilities.** Should a package declare the contexts it requires in its manifest, so the index can
  show "this package needs `FileSystem`" before anyone installs it?
- **Private packages.** Should the client support more than one index (a company's own) from the start? The
  static protocol makes it cheap: a list of index URLs in user configuration.
