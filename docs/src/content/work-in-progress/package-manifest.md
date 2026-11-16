---
title: "The package manifest"
description: "Why the .nyxp manifest is a data-only Nanyx record, how the compiler reads it, and what is still open"
order: 32
---

The manifest is built ([#1404](https://github.com/nanyx-lang/nanyx/issues/1404)): its format is described in
[Packages and projects](../tour/packages#package-manifests), and a script's directives, which say what a manifest
would, in [Files and projects](../getting-started/files-and-projects#directives). This page keeps why it is Nanyx, how
the compiler reads it, and what is still open. It is part of the [packaging](./packaging) workstream.

# Why not TOML

TOML suited a manifest of flat metadata and version strings. The manifest that [packaging](./packaging#local-packages-first)
and [Built-ins as packages](./built-ins-as-packages#the-manifest) need relies on the two things TOML does worst:

- **Dotted package names.** Every Nanyx package name has a dot (`nanyx.lint`), and in a TOML table header it must be
  quoted. `[linters."nanyx.lint"]` works, but `[linters.nanyx.lint]` silently means a table `lint` inside a table
  `nanyx`.
- **Typed settings for packages.** A linter or a host declares the settings it accepts, so that a misspelt key is an
  error. In TOML that needs a schema language of our own, a mapping from TOML values to Nanyx types, and error
  messages of its own: a second, weaker type checker for one file.

The manifest is data only, so that it can always be read without running or type-checking a program, which
`nanyx install` needs in order to edit it. Dependencies and linters are lists of records with a `package` field, not
maps, so every entry has the same shape and no name needs quoting.

# How it's read

- **Its shape is checked** by `Manifest.check`: the top-level fields, `package`'s `name`, `version`, `sources` and
  `host`, and each entry's fields. Code is `NYX0020`, an unknown field `NYX0021`, a missing `name` `NYX0022`, a
  wrong value `NYX0023` (a package listed twice in `dependencies` among them) and a field that isn't supported yet
  `NYX0024`, each at that field. A manifest in the old TOML form is `NYX0034` at its first line
  ([#1596](https://github.com/nanyx-lang/nanyx/issues/1596)).
- **A manifest that doesn't read stops its files.** A source file under a manifest with errors is not checked as a
  script: `nanyx check <file>`, `nanyx run <file>` and the language server report the manifest's errors in place of
  the file's own (`Package.modulesFor`).
- **A script's directives** are read by `Script.read` into the manifest entries they stand for, and checked with the
  manifest's own readers (`Manifest.hostEntry`, `dependencyEntries`, `linterEntries`), so an error is the manifest's,
  at the word it is about ([#1599](https://github.com/nanyx-lang/nanyx/issues/1599)); `nanyx migrate` writes those
  entries into a manifest. A directive in the old form (`::linters.default`) is `NYX0035`, with a fix that rewrites it,
  and a `::lint` with no `::linter` above it is `NYX0036`.
- **Tools** edit the record and reformat it with the formatter, so a manifest that `nanyx install` or `nanyx new`
  touched still looks hand-written. `nanyx manifest --json` prints it for tools outside the compiler, such as the
  package index's registration check in `nanyx-lang/packages`.

# What's not built

- **Typed settings.** A linter or host exports its settings as a record type, and the entry's `settings` is checked
  against it with the ordinary type checker, so a misspelt key is an ordinary type error, with a span, naming the
  package. It needs the package's module compiled first, which happens anyway before it runs. It waits for linters
  ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)) to be packages that can declare them, and for a host that
  takes settings. Until then, the compiler checks the settings it knows itself: a linter's `rules`, and no host takes
  any (`NYX0024`).
- **Hover and completion in the manifest.** The extension highlights and formats a `.nyxp`, and the language server
  checks it as you type, but there is no hover or completion in it yet; completion on a `settings` field is to list
  the package's settings.
- **The formatter as a configured tool.** Its settings are the `formatter` field, which the formatter reads itself. It
  could become a configured tool like a linter later.

# Open questions

- **Version ranges.** A string (`"^2.1"`, `"2.1"` meaning caret, as [Packaging](./packaging#versions-and-resolution)
  says), or tags (`#caret("2.1")`, `#exact("0.28.0")`)? Versions are strings for now: a linter's or host's `version`
  is an exact version string, `"0.28.0"`.
