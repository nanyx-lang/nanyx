---
title: "The package manifest"
description: "What is still open for the .nyxp manifest: typed settings, editor support and version ranges"
order: 32
---

The manifest is built ([#1404](https://github.com/nanyx-lang/nanyx/issues/1404)): a data-only Nanyx record, described
in [Packages and projects](../tour/packages#package-manifests), with its errors (`NYX0020` to `NYX0024`, `NYX0034` to
`NYX0036`) in [Errors](../standard-tools/errors). A script's directives, which say what a manifest would, are in
[Files and projects](../getting-started/files-and-projects#directives). It is Nanyx rather than TOML because every
package name has a dot, which a TOML table header must quote, and because settings a package declares can be checked
as Nanyx values. This page keeps what is still open. It is part of the [packaging](./packaging) workstream.

# What's not built

- **Settings typed by a record type.** A host declares the settings it takes in its own manifest, with their
  defaults, and a program's `settings` for it are checked against them: a setting the host doesn't take is `NYX0021`,
  and one of another kind than its default `NYX0023` ([#1786](https://github.com/nanyx-lang/nanyx/issues/1786)). The
  plan is for a linter or host to export its settings as a record type instead, and for the entry's `settings` to be
  checked against it with the ordinary type checker, so a misspelt key is an ordinary type error, with a span, naming
  the package. It needs the package's module compiled first, which happens anyway before it runs. For linters it waits
  on linters being packages ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)); until then a linter's
  `settings` hold only `rules`, which the compiler checks itself.
- **Hover and completion in the manifest.** The extension highlights and formats a `.nyxp`, and the language server
  checks it as you type, but there is no hover or completion in it yet; completion on a `settings` field is to list
  the package's settings.
- **The formatter as a configured tool.** Its settings are the `formatter` field, which the formatter reads itself. It
  could become a configured tool like a linter later.

# Open questions

- **Version ranges.** A string (`"^2.1"`, `"2.1"` meaning caret, as [Packaging](./packaging#versions-and-resolution)
  says), or tags (`#caret("2.1")`, `#exact("0.28.0")`)? Versions are strings for now: a linter's or host's `version`
  is an exact version string, `"0.28.0"`.
