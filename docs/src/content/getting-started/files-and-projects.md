---
title: "Files and projects"
description: "How Nanyx runs single files and multi-file projects"
order: 4
---

You can run a single Nanyx file directly, or you can organize code as a multi-file project. The compiler supports both workflows.

# Single-file scripts

A single file can be executed on its own. It does not need module or members declarations; you can write imports and top-level statements directly.

```nanyx
import (
  nanyx/file
  nanyx/string
)

match file.read("input.txt")
  | #some(content) ->
    def words = content
      \split(on = " ")
      \filter { != "" }
    println("Word count: {words.length}")
  | #error(err) ->
    println("Error: {err}")
```

You can run this file with `nanyx run filename.nyx`. The compiler treats the file as an implicit module and executes top-level statements. This is great for quick scripts, experiments, and learning.

## Directives

A script has no package manifest, but it can say what a manifest would with _directives_: lines starting `::` at the top of the file, before its first line of code. They may come after a `#!` line and among comments and blank lines.

A directive is written the way the manifest's TOML would write it, as a dotted key after `::`. A key on its own stands for a table. A key followed by `= value` sets that value, which is a TOML value, so strings are quoted:

```nanyx
#!/usr/bin/env -S nanyx run
::linters.default.unusedBindings = "error"
::formatter.lineWidth = 80

import nanyx/console

println("Hello!")
```

| Directive | What it does | In a manifest |
| --- | --- | --- |
| `::linters.default` | Turns a linter on, with every rule at its default severity | `[linters.default]` |
| `::linters.default.unusedBindings = "error"` | Turns the linter on and sets one rule's severity (`"error"`, `"warning"`, `"info"`, `"hint"` or `"none"`) | `unusedBindings = "error"` under `[linters.default]` |
| `::formatter.lineWidth = 80` | Sets one of the formatter's settings, `lineWidth` or `indentSize` | `lineWidth = 80` under `[formatter]` |
| `::dependencies.acme.json = "1.2"` | Depends on a package: not supported yet, so it is an error for now | `acme.json = "1.2"` under `[dependencies]` |

As in a manifest, only the linters a script's `::linters` directives name run, so a script without one runs none. A directive the compiler does not understand, or one after the first line of code, is an error naming its line. A file that belongs to a package cannot have directives: its manifest is the one place a package says these things.

# Importing by file path

Files can import each other by file path. An import by file path needs an alias, and the file it names must be a script: one without a `module name` declaration.

```nanyx
-- utils.nyx
export def someHelper = { ... }
```

```nanyx
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

## Package manifest format (TOML)

A package is described by a `.nyxp` file at the package root, written in TOML. It names the package, selects its source files, and lists its dependencies:

```toml
[package]
name = "word_counter"
version = "0.1.0"
sources = ["src/**/*.nyx"]

[dependencies]
nanyx.web = "1.4"
```

`nanyx new` creates this file for you. The full manifest format, and how packages are grouped into projects, is covered in [Packages and projects](../tour/packages).

When a package manifest is present, modules are imported by name, and file-path imports are not used.

As a rule of thumb:

- Use single files for quick scripts or experiments.
- Use file-path imports for small, ad-hoc multi-file code.
- Use a package manifest and module imports for longer-lived codebases.
