---
title: "The package manifest"
description: "Proposal: the .nyxp manifest becomes a data-only Nanyx record instead of TOML"
order: 32
---

A package's manifest, its `.nyxp` file, is TOML today. This page proposes writing it in Nanyx instead, as a single
record value with no code in it. The maintainer decided the switch, before path dependencies
([#862](https://github.com/nanyx-lang/nanyx/issues/862)) add `[dependencies]` to the TOML form. It is part of the
[packaging](./packaging) workstream.

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
  package = (
    name = "emails"
    version = "0.1.0"
    sources = ["src/**/*.nyx"]
  )
  dependencies = [
    (package = "acme.util", path = "../util")
  ]
  linters = [
    (
      package = "nanyx.lint"
      version = "0.28.0"
      settings = (lineWidth = 100, rules = (unusedBindings = #error, nonTailRecursion = #off))
    )
  ]
  host = (package = "nanyx.console", version = "0.28.0")
)
```

- **Lists of entries, not maps.** Dependencies and linters are lists of records with a `package` field, so every
  entry has the same shape and no name needs quoting. `host` is one entry.
- **What it may contain.** Only record, list and tuple literals, strings, numbers, `true` and `false`, and tags
  (`#error`). Anything else is an error with a span: a name, a call, an operator, a lambda, an interpolation. The
  manifest can always be read without running or type-checking a program, which `nanyx add` needs in order to edit
  it.
- **Its shape is checked.** The fields are the manifest's own: `package`, `dependencies`, `linters` and `host` at the
  top, and `name`, `version`, `sources` and `public-modules` (as `publicModules`) in `package`. An unknown field, a
  missing `name` or a string where a list is expected is an error at that field.
- **Settings are typed by their package.** A linter or host exports its settings as a record type, and the entry's
  `settings` is checked against it with the ordinary type checker. A misspelt key is an ordinary type error, with a
  span, naming the package. It needs the package's module compiled first, which happens anyway before it runs.
- **Nothing implicit.** With no `linters`, no linter runs; with no `host`, the package can't be run or built. `nanyx
  new` writes both, as [Built-ins as packages](./built-ins-as-packages#the-manifest) decided.
- **The file** keeps its name and extension, `<name>.nyxp`, so finding a package is unchanged. A `.nyxp` file is
  Nanyx source to the parser, the formatter and the editor. Scripts keep their `::` directives.

# Tools

- **The editor.** The extension treats `.nyxp` as Nanyx, so highlighting, the formatter, diagnostics, hover and
  completion work in the manifest. Completion on a `settings` field lists the package's settings.
- **Tools outside the compiler.** Scripts and CI that read a manifest, such as the package index's registration
  check in `nanyx-lang/packages`, use `nanyx manifest --json`, which prints the manifest as JSON.
- **Editing it.** `nanyx add` and `nanyx new` edit the record and reformat it with the formatter, so a manifest the
  tools touched still looks hand-written.

# Plan

1. **Read the manifest as Nanyx** ([#1404](https://github.com/nanyx-lang/nanyx/issues/1404)): the parser reads a `.nyxp` file as a record value, a manifest checker enforces
   the data-only rule and the manifest's own shape, and every reader of the TOML form uses it. That means
   `Package.fs`, the language server, the formatter's settings, `nanyx new` and `examples/greeter/greeter.nyxp`.
   TOML support is removed, since Nanyx is in closed alpha.
2. **`nanyx manifest --json`.**
3. **Path dependencies** in the new form ([#862](https://github.com/nanyx-lang/nanyx/issues/862)), then the rest of
   [local packages](./packaging#local-packages-first).
4. **Typed settings**, once linters ([#1343](https://github.com/nanyx-lang/nanyx/issues/1343)) and hosts
   ([#1344](https://github.com/nanyx-lang/nanyx/issues/1344)) are packages that can declare them.

# Open questions

- **Version ranges.** A string (`"^2.1"`, `"2.1"` meaning caret, as [Packaging](./packaging#versions-and-resolution)
  says), or tags (`#caret("2.1")`, `#exact("0.28.0")`)?
- **The formatter's settings.** They sit in the TOML today as `[formatter]`. They could become a `formatter` field, or
  the formatter could become a configured tool like a linter.
