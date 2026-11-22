---
title: "Distribution"
description: "The package managers the compiler doesn't reach yet (Homebrew, apt, an install script), and signing"
order: 35
---

The compiler is released on GitHub for each platform, and reaches users through Scoop and the VS Code extension. How
to install it is on [Installation](../getting-started/installation), and how a release is made on
[Releases](../deep-dives/releases) in Deep dives. This page keeps what is planned and the open questions.

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
