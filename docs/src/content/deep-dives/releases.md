---
title: "Releases"
description: "How the compiler is built for each platform, released on GitHub, and picked up by Scoop and the VS Code extension"
order: 21
---

How to install the compiler is on [Installation](../getting-started/installation). This page says how a release is
made and how it reaches users. The package managers still to come are issues: a Homebrew tap
([#2733](https://github.com/nanyx-lang/nanyx/issues/2733)), an apt repository
([#2734](https://github.com/nanyx-lang/nanyx/issues/2734)) and an install script
([#2737](https://github.com/nanyx-lang/nanyx/issues/2737)), and so is signing the binaries
([#2740](https://github.com/nanyx-lang/nanyx/issues/2740)).

# The binaries

The `Compiler` workflow (`.github/workflows/compiler.yml`) publishes `nanyx` for five runtime identifiers:
`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. It builds them on a push to `main`, and on a run of
a branch only when the branch's version is new, with no `compiler/<version>` tag yet, as a release PR's is. Its
`Version` job reads the version and looks for the tag, and the publish jobs wait for it. Each build is self-contained and a single file, so
no .NET runtime is needed, and is packed as `nanyx-<version>-<rid>.zip` on Windows and `nanyx-<version>-<rid>.tar.gz`
elsewhere. An archive holds the executable, `nanyx.exe` or `nanyx` (with its executable bit set), and beside it the
files the compiler reads from there: the checked standard library and `packages/`, the
[packages the compiler ships](../advanced/hosts#packages-the-compiler-ships) as source directories
([Shipped packages](./shipped-packages)).

The workflow smoke-tests the `linux-x64` binary: it runs `--help`, a one-line script, and a package `nanyx new` made,
so an archive without its packages, or whose embedded Wasmtime library didn't survive single-file publishing, fails.

# The release

Merging a change to the compiler's `<Version>` on `main` is the release. The `Release` job, which runs only on `main`
and after the tests, the browser test, the `Version` job and the publish jobs, tags the commit `compiler/<version>` and attaches the
archives it has just built and tested to a GitHub release named `Compiler <version>`, with the browser build,
`nanyx-browser.zip`, that the docs site's playground runs ([Playground](./playground#the-measurement)). When that tag
already exists, it does nothing, and releases are queued, not raced, so a second push of the same version sees the
first one's tag. Nothing is rebuilt for a release.

The release's notes are the version's section of the [Changelog](../overview/changelog), with its relative links made
absolute. The `Version` job reads the section on every run whose version is new, on a branch as on `main`, so a version
bump without its section fails before it is merged. What a release PR must change is in `AGENTS.md`, under "Releasing the compiler".

The binaries aren't signed, so macOS quarantines a browser download until the user removes the flag.

# The shipped packages

Each release also publishes the [packages the compiler ships](./shipped-packages) to the
[package index](./package-index), as any package is published: the publish job packs each directory under `packages/`
with the `nanyx` it has just built, `nanyx pack`, and the release attaches each `<name>-<version>.nyxpkg` beside the
binaries. The job fails, before anything is released, if a package doesn't pack or if fewer packages are packed at the
compiler's version than `packages/` holds.

The index registers each shipped package with this repository, and its sync reads the version from the release's tag
after its `compiler/` prefix, records each package's asset, its SHA-256 and its manifest's `nanyx`, and runs the same
checks as on any release. So a release reaches the index within the hour, and needs no token for the index's
repository. Each shipped manifest gives `repository = "nanyx-lang/nanyx"`, which the sync checks against the
registration, and a `nanyx`: the compiler's major and minor version for a library, `0.49`, and the compiler's version
only for a host or a host library, `=0.49.0`, since they use the compiler's internals. The resolver never chooses one
of those for another compiler.

# Scoop

The bucket is the [`nanyx-lang/scoop-nanyx`](https://github.com/nanyx-lang/scoop-nanyx) repository, with one
manifest, `bucket/nanyx.json`, pointing at the `win-x64` zip.

The bucket's `excavator.yml` workflow runs Scoop's `checkver` every day. When it finds a newer `compiler/<version>`
release, it updates the manifest's `version`, `url` and `hash` and commits the change, so the bucket needs no editing by
hand on a release. A new release reaches Scoop users within a day.

# The VS Code extension

The extension lists the `compiler/<version>` releases, downloads the archive for the user's platform into its own
storage, and checks at most once a day for a newer one. See
[VS Code extension](../standard-tools/vs-code-extension#finding-the-compiler).
