---
title: "Files and projects"
description: "How Nanyx runs single files and multi-file projects"
order: 4
---

You can run a single Nanyx file directly, or you can organize code as a multi-file project. The compiler supports both workflows.

# Single-file scripts

A single file can be executed on its own. It does not need module or members declarations; you can write imports and top-level statements directly.

```nanyx
::install nanyx.fs

import (
  nanyx/text
  nanyx.fs/file as file
  nanyx.fs/path as path
)

match file.read(path.of("input.txt"))
  | #some(content) ->
    def words = content
      \split(on = " ")
      \filter { != "" }

    println("Word count: {words.length}")
  | err ->
    println("Error: {err}")
```

You can run this file with `nanyx run filename.nyx`. The compiler treats the file as an implicit module and executes top-level statements. Its first line is a directive (see [Directives](#directives) below): it lets the script import the shipped package `nanyx.fs`, whose [`file`](../stdlib/file) module reads files. This is great for quick scripts, experiments, and learning.

## Directives

A script has no package manifest, but it can say what a manifest would with _directives_: lines starting `::` at the top of the file, before its first line of code. They may come after a `#!` line and among comments and blank lines.

A directive is a name after `::`, then words separated by spaces, all on one line. A value is written as in a manifest: a tag, `#error`, a number, `80`, or a string in quotes. A `--` comment may follow:

```nanyx
#!/usr/bin/env -S nanyx run
::linter nanyx.lint
::lint unusedBindings #error
::format lineWidth 80

println("Hello!")
```

| Directive | What it does | In a manifest |
| --- | --- | --- |
| `::host nanyx.cli.host` | Runs the script under a host the compiler ships; without `::host`, a script runs under `nanyx.cli.host` | `host = (package = "nanyx.cli.host", version = "0.52.1")` |
| `::host acme.server ../server` | Runs the script under the host package in a directory, relative to the script | `host = (package = "acme.server", path = "../server")` |
| `::install acme.json ../json` | Depends on the package in a directory, so the script can import its public modules, `import acme.json/parse` | `dependencies = [(package = "acme.json", path = "../json")]` |
| `::install acme.json 1.2.0` | Depends on a package from the [package index](../tour/packages#package-dependencies), at exactly that version | `dependencies = [(package = "acme.json", version = "1.2.0")]`, locked in `nanyx.lock` |
| `::install nanyx.cli` | Depends on a package the compiler ships | `dependencies = [(package = "nanyx.cli", version = "0.52.1")]` |
| `::linter nanyx.lint` | Turns the official linter on, with every rule at its default severity | `linters = [(package = "nanyx.lint", version = "0.52.1")]` |
| `::lint unusedBindings #error` | Sets a rule's severity (`#error`, `#warning`, `#info`, `#hint` or `#off`) for the linter of the nearest `::linter` above it | `settings = (rules = (unusedBindings = #error))` in that linter's entry |
| `::format lineWidth 80` | Sets one of the formatter's settings, `lineWidth` or `indentSize` | `formatter = (lineWidth = 80)` |

`::host`, `::install` and `::linter` take a package's name and then, if it isn't one the compiler ships, where it is found: a version, which starts with a digit (`1.2.0`), or a directory, which starts with `.` or `/` (`../util`), relative to the script. A package the compiler ships, named on its own, is at the compiler's version, and a version given for one must be the compiler's.

A script has no lockfile, so a package from the index is named at the exact version it uses, `1.2.0`, not the versions
it accepts, `1.2`: the directive is the pin. Named with no version it is an error, whose fix writes the newest version
the index has. The editor asks the index for it when the fix is asked for, not as the name is typed. `nanyx run` downloads each version a script pins into the package cache, with the packages it depends
on, the newest each accepts, and checks each download against the SHA-256 the index records, as `nanyx pull script.nyx`
does; `nanyx check` and the editor read them from the cache. Once the cache holds every package the script needs,
`nanyx run` reads nothing from the index, so a script runs without a network; `nanyx pull script.nyx` reads the index
whatever the cache holds. A version that has been unlisted still resolves when a script names it, with a warning
([`NYX0051`](../standard-tools/errors#nyx0051)), which `nanyx run` gives when it downloads it.

As in a manifest, only the linters a script's `::linter` lines name run, so a script without one runs none. A `::lint` with no `::linter` above it is an error, whose fix turns the official linter on above it. A wrong `::host` or `::install` is the error a manifest's entry would be, at the word it is about. A directive the compiler does not understand, one written as a manifest's key and a TOML value (`::linters.default`, which the error's fix rewrites), or one after the first line of code, is an error. A file that belongs to a package cannot have directives: its manifest is the one place a package says these things. When a script grows into a package, `nanyx migrate` writes its manifest from its directives and makes the script the package's `@entry` (see [`nanyx migrate`](../standard-tools/command-line#nanyx-migrate)).

# Importing by file path

Files can import each other by file path. An import by file path needs an alias, and the file it names must be a script: one without a `module name` declaration.

```nanyx fragment
-- utils.nyx
export def someHelper = { ... }
```

```nanyx fragment
-- main.nyx
import "./utils" as utils

def main = {
  utils.someHelper(...)
}

main()
```

This works well for small scripts, but there is one important rule: a file with top-level statements is an entrypoint, and entrypoints cannot be imported. If you try, the compiler will error.

That means shared files should avoid top-level statements and only define values, functions, types, and modules.

# Packages and module imports

For larger projects, turn the code into a _package_ by adding a package manifest. In that mode, every source file declares a named module, and modules are imported by their declared names instead of by file paths. This keeps imports stable as the project grows and clarifies ownership of modules.

## Package manifest format

A package is described by a `.nyxp` file at the package root: one Nanyx record, written as data, with no code in it. It names the package, selects its source files, and says which linters check them and which host runs them:

```nanyx fragment
(
  package = (name = "word_counter", version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [(package = "nanyx.lint", version = "0.52.1")]
  host = (package = "nanyx.cli.host", version = "0.52.1")
)
```

`nanyx new` creates this file for you. The full manifest format, and how packages are grouped into projects, is covered in [Packages and projects](../tour/packages).

When a package manifest is present, modules are imported by name, and file-path imports are not used. A file under a manifest that has errors is not checked on its own: checking or running it reports the manifest's errors.

As a rule of thumb:

- Use single files for quick scripts or experiments.
- Use file-path imports for small, ad-hoc multi-file code.
- Use a package manifest and module imports for longer-lived codebases.
