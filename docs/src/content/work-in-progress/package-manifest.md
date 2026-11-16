---
title: "The package manifest"
description: "The .nyxp manifest: a data-only Nanyx record"
order: 32
---

A package's manifest, its `.nyxp` file, is written in Nanyx, as a single record value with no code in it. The
maintainer chose this over TOML before path dependencies ([#862](https://github.com/nanyx-lang/nanyx/issues/862))
add `dependencies`. It is part of the [packaging](./packaging) workstream.

# Why not TOML

TOML suited a manifest of flat metadata and version strings. The manifest that [packaging](./packaging#local-packages-first)
and [Built-ins as packages](./built-ins-as-packages#the-manifest) now need relies on the two things TOML does worst:

- **Dotted package names.** Every Nanyx package name has a dot (`nanyx.lint`), and in a TOML table header it must be
  quoted. `[linters."nanyx.lint"]` works, but `[linters.nanyx.lint]` silently means a table `lint` inside a table
  `nanyx`.
- **Typed settings for packages.** A linter or a host declares the settings it accepts, so that a misspelt key is an
  error. In TOML that needs a schema language of our own, a mapping from TOML values to Nanyx types, and error
  messages of its own: a second, weaker type checker for one file.

# The format

The manifest is one Nanyx record value. It holds only data: no imports, no definitions, no calls, no interpolation.

```nanyx fragment
(
  package = (name = "emails", version = "0.1.0", sources = ["src/**/*.nyx"])
  dependencies = [
    (package = "acme.util", path = "../util")
  ]
  linters = [
    (
      package = "nanyx.lint"
      version = "0.28.0"
      settings = (rules = (unusedBindings = #error, nonTailRecursion = #off))
    )
  ]
  host = (package = "nanyx.console", version = "0.28.0")
  formatter = (lineWidth = 100)
)
```

- **Lists of entries, not maps.** Dependencies and linters are lists of records with a `package` field, so every
  entry has the same shape and no name needs quoting. `host` is one entry.
- **What it may contain.** Only record, list and tuple literals, strings, numbers, `true` and `false`, tags
  (`#error`) and `--` comments. Anything else is an error with a span (`NYX0020`): a name, a call, an operator, a
  lambda, an interpolated string, a definition, an import, a `::` directive. The manifest can always be read without
  running or type-checking a program, which `nanyx install` needs in order to edit it.
- **Its shape is checked** (`Manifest.check`). The fields are the manifest's own: `package`, `dependencies`,
  `linters`, `host` and `formatter` at the top, and `name`, `version`, `sources` and `host` in
  `package`. A linter entry has `package`, `version` and `settings`, and a host entry `package`, `version` or `path`,
  and `settings`. A host package's `package` declares it one with `host = (root = "<module>", prelude = "<module>")`
  ([A host package](./hosts#a-host-package)). An unknown field (`NYX0021`), a missing `name`
  (`NYX0022`), a string where a list is expected (`NYX0023`) or a field that isn't supported yet (`NYX0024`) is an
  error at that field. A package listed twice in `dependencies` is `NYX0023` at the repeat. Which modules other
  packages may import is no field of the manifest: a module says it's exported with `export module` in its own
  declaration ([#1457](https://github.com/nanyx-lang/nanyx/issues/1457), [What a package exports](./packaging#what-a-package-exports)).
- **A manifest in the old TOML form** is `NYX0034` at its first line, in place of the parser's error, when that line
  is a table header (`[package]`) or a key (`name = "shop"`)
  ([#1596](https://github.com/nanyx-lang/nanyx/issues/1596)).
- **A manifest that doesn't read stops its files.** A source file under a manifest with errors is not checked as a
  script: `nanyx check <file>`, `nanyx run <file>` and the language server report the manifest's errors, at the
  manifest, in place of the file's own, since which files are the package's and what its imports name can't be known
  without it (`Package.modulesFor`, [#1596](https://github.com/nanyx-lang/nanyx/issues/1596)).
- **What works today.** A dependency gives a `path`, or names a package the compiler ships, such as
  `nanyx.console.core`, at the compiler's version exactly (`NYX0029` otherwise); any other with a `version` is
  `NYX0024` until packages can be downloaded, and one with both is an error. The only linter is `nanyx.lint`, whose
  `settings` hold `rules`, each set to `#error`, `#warning`, `#info`, `#hint` or `#off`. A host is one the compiler
  ships, `nanyx.console`, named at the compiler's version, or one found by its `path`, with no version. No host takes
  `settings` yet.
- **Settings are typed by their package.** A linter or host exports its settings as a record type, and the entry's
  `settings` is checked against it with the ordinary type checker. A misspelt key is an ordinary type error, with a
  span, naming the package. It needs the package's module compiled first, which happens anyway before it runs. Until
  then, the compiler checks the settings it knows itself.
- **The formatter's settings** are the `formatter` field, `(lineWidth = 100, indentSize = 2)`, which the formatter
  reads itself. It is the simplest place for them; the formatter could become a configured tool like a linter later.
- **Nothing implicit.** With no `linters`, no linter runs; with no `host`, the package is a library: an `@entry` in it
  is `NYX0031`, and running it is an error that says it has no entry point. `nanyx new` writes both, as
  [Built-ins as packages](./built-ins-as-packages#the-manifest) decided.
- **The file** keeps its name and extension, `<name>.nyxp`, so finding a package is unchanged. A `.nyxp` file is
  Nanyx source to the parser and the formatter.
- **A script's directives** say what a manifest would, one entry per line, as short lines of words rather than the
  manifest's record syntax, which is too long for a directive ([#1599](https://github.com/nanyx-lang/nanyx/issues/1599)):
  `::host nanyx.console`, `::dependency acme.util ../util`, `::linter nanyx.lint`, `::lint unusedBindings #error` (for
  the linter of the nearest `::linter` above it) and `::format indentSize 4`. A package's name is followed by a
  version, which starts with a digit, a directory, which starts with `.` or `/`, or neither for a package the compiler
  ships. Values are written as in a manifest. `Script.read` turns them into the manifest entries they stand for and
  checks them with the manifest's own readers (`Manifest.hostEntry`, `dependencyEntries`, `linterEntries`), so an
  error is the manifest's, at the word it is about; `nanyx migrate` writes those entries into a manifest. A directive
  in the form directives had before, a manifest's dotted key with a TOML value (`::linters.default`), is `NYX0035`,
  with a fix that rewrites it, and a `::lint` with no `::linter` above it is `NYX0036`, whose fix adds
  `::linter nanyx.lint` above it.

# Tools

- **The editor.** The extension treats `.nyxp` as Nanyx: it highlights and formats the manifest, and the language
  server checks it as you type, showing each problem at its field. Hover and completion in the manifest are still to
  come; completion on a `settings` field is to list the package's settings.
- **Tools outside the compiler.** Scripts and CI that read a manifest, such as the package index's registration
  check in `nanyx-lang/packages`, use `nanyx manifest --json`, which checks the manifest and prints it as JSON: a record is an object, a list or
  tuple an array, and a tag a string, `"#error"` (or `{ "#name": payload }` for a tag with a payload).
- **Editing it.** `nanyx install` and `nanyx new` edit the record and reformat it with the formatter, so a manifest the
  tools touched still looks hand-written.

# Plan

1. ~~**Read the manifest as Nanyx** ([#1404](https://github.com/nanyx-lang/nanyx/issues/1404)): the parser reads a
   `.nyxp` file as a record value, a manifest checker enforces the data-only rule and the manifest's own shape, and
   every reader of the TOML form uses it. That means `Package.fs`, the language server, the formatter's settings,
   `nanyx new` and `examples/greeter/greeter.nyxp`. TOML support is removed, since Nanyx is in closed alpha.~~ Done.
2. ~~**`nanyx manifest --json`.**~~ Done.
3. ~~**Path dependencies** in the new form ([#862](https://github.com/nanyx-lang/nanyx/issues/862))~~ Done; then the
   rest of [local packages](./packaging#local-packages-first).
4. **Typed settings**, once linters ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)) and hosts
   ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)) are packages that can declare them.
5. ~~**The editor** treats `.nyxp` as Nanyx.~~ Done, apart from hover and completion in the manifest.

# Open questions

- **Version ranges.** A string (`"^2.1"`, `"2.1"` meaning caret, as [Packaging](./packaging#versions-and-resolution)
  says), or tags (`#caret("2.1")`, `#exact("0.28.0")`)? Versions are strings for now: a linter's or host's `version`
  is an exact version string, `"0.28.0"`.
