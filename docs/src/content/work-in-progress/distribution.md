---
title: "Distribution"
description: "How the compiler reaches users: GitHub releases, the Scoop bucket, and the package managers still to come"
order: 35
---

> **Status: outline.** This page describes how the `nanyx` compiler is distributed today and what is planned. The
> user-facing steps are on [Installation](../getting-started/installation).

# Summary

`nanyx` is published as a self-contained binary for each platform, attached to a GitHub release. Windows users can
also install it with Scoop, and the VS Code extension downloads it on its own. Homebrew, apt and an install script
are not there yet.

# What exists

## GitHub releases

The `Compiler` workflow (`.github/workflows/compiler.yml`) publishes `nanyx` for five runtime identifiers:
`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. Each build is self-contained, so no .NET runtime
is needed, and is packed as `nanyx-<version>-<rid>.zip` on Windows and `nanyx-<version>-<rid>.tar.gz` elsewhere. An
archive holds the executable, `nanyx.exe` or `nanyx` (with its executable bit set), and beside it `packages/`, the
packages the compiler ships as source directories (the console host, `packages/nanyx.console`, and the others
[Hosts](../advanced/hosts#packages-the-compiler-ships) lists), which it finds there. The workflow's smoke test runs a script and a package `nanyx new`
made with the published binary, so an archive without them fails.

Merging a change to the compiler's version on `main` is the release: the `Release` job tags the commit
`compiler/<version>` and attaches the archives it has just built and tested to a GitHub release, unless that tag
already exists. Nothing is rebuilt for a release. The release's notes are the version's section of the
[Changelog](../overview/changelog), and the workflow fails when there is no section for the version. The binaries aren't signed, so macOS quarantines a browser
download until the user removes the flag.

## Scoop

The bucket is the [`nanyx-lang/scoop-nanyx`](https://github.com/nanyx-lang/scoop-nanyx) repository, with one
manifest, `bucket/nanyx.json`, pointing at the `win-x64` zip. Users add it with
`scoop bucket add nanyx https://github.com/nanyx-lang/scoop-nanyx`.

The bucket's `excavator.yml` workflow runs Scoop's `checkver` every day. When it finds a newer `compiler/<version>`
release it updates the manifest's `version`, `url` and `hash` and commits the change, so the bucket needs no editing
by hand on a release. A new release reaches Scoop users within a day.

## The VS Code extension

The extension lists the `compiler/<version>` releases, downloads the archive for the user's platform into its own
storage, and checks at most once a day for a newer one. See
[VS Code extension](../standard-tools/vs-code-extension#finding-the-compiler).

# What's planned

- **A Homebrew tap**, such as `nanyx-lang/homebrew-nanyx`, with a formula pointing at the `osx-*` and `linux-*`
  archives, so that `brew install nanyx-lang/nanyx/nanyx` works on macOS and Linux.
- **An apt repository** serving a `.deb` built from the `linux-*` archives.
- **Possibly an install script**, such as `curl -fsSL https://nanyx.dev/install | sh`, that downloads the right
  archive and puts `nanyx` on the `PATH`. The [Installation](../getting-started/installation) page must not mention
  one until it exists.

# Open questions

- **Signing.** Should the macOS binaries be signed and notarised, and the Windows ones signed, so they run without
  warnings? Who holds the keys, and where do the secrets live in CI?
- **Hosting.** Where does the apt repository live (GitHub Pages, a package host), and who signs its index?
- **Updating on release.** The Scoop bucket polls daily. Should the Homebrew tap and apt repository do the same, or
  should the `Release` job push to them directly?
- **The install script's home.** `nanyx.dev/install` would be served by the docs site; does it pin a version, and
  how does it verify what it downloads?
