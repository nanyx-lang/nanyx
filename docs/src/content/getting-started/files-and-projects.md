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

A directive is a dotted key after `::`. A key on its own turns something on. A key followed by `= value` sets that value: a string in quotes, a number, or a table in braces, `{ path = "../json" }`:

```nanyx
#!/usr/bin/env -S nanyx run
::linters.default.unusedBindings = "error"
::formatter.lineWidth = 80

println("Hello!")
```

| Directive | What it does | In a manifest |
| --- | --- | --- |
| `::linters.default` | Turns the official linter on, with every rule at its default severity | `linters = [(package = "nanyx.lint", version = "0.28.0")]` |
| `::linters.default.unusedBindings = "error"` | Turns the linter on and sets one rule's severity (`"error"`, `"warning"`, `"info"`, `"hint"` or `"off"`) | `settings = (rules = (unusedBindings = #error))` in that linter's entry |
| `::formatter.lineWidth = 80` | Sets one of the formatter's settings, `lineWidth` or `indentSize` | `formatter = (lineWidth = 80)` |
| `::host.nanyx.console = "0.28.0"` | Runs the script under a host the compiler ships, at the compiler's version; without `::host`, a script runs under `nanyx.console` | `host = (package = "nanyx.console", version = "0.28.0")` |
| `::host.acme.server = { path = "../server" }` | Runs the script under the host package in a directory, relative to the script | `host = (package = "acme.server", path = "../server")` |
| `::dependencies.acme.json = { path = "../json" }` | Depends on the package in a directory, so the script can import its public modules, `import acme.json/parse` | `dependencies = [(package = "acme.json", path = "../json")]` |
| `::dependencies.nanyx.console.core = "0.28.0"` | Depends on a package the compiler ships, at the compiler's version | `dependencies = [(package = "nanyx.console.core", version = "0.28.0")]` |

As in a manifest, only the linters a script's `::linters` directives name run, so a script without one runs none. A wrong `::host` or `::dependencies` is the error a manifest's entry would be, at the directive. A directive the compiler does not understand, or one after the first line of code, is an error naming its line. A file that belongs to a package cannot have directives: its manifest is the one place a package says these things.

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

## Package manifest format

A package is described by a `.nyxp` file at the package root: one Nanyx record, written as data, with no code in it. It names the package, selects its source files, and says which linters check them and which host runs them:

```nanyx fragment
(
  package = (name = "word_counter", version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [(package = "nanyx.lint", version = "0.28.0")]
  host = (package = "nanyx.console", version = "0.28.0")
)
```

`nanyx new` creates this file for you. The full manifest format, and how packages are grouped into projects, is covered in [Packages and projects](../tour/packages).

When a package manifest is present, modules are imported by name, and file-path imports are not used.

As a rule of thumb:

- Use single files for quick scripts or experiments.
- Use file-path imports for small, ad-hoc multi-file code.
- Use a package manifest and module imports for longer-lived codebases.
