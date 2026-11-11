---
title: "Formatter"
description: "Formatting Nanyx code with the standard formatter"
order: 2
---

Nanyx includes a standard formatter for producing consistent code layout across teams and projects.

The formatter focuses on whitespace, indentation, line wrapping, and other presentational concerns. It does not change program behavior.

The formatter is available as a command line tool and can be integrated into editor workflows.

The formatter is minimally configurable by design, with the goal of keeping style consistent across contributors and reducing formatting-only diffs in reviews. It also works well with linter guidance and editor tooling to help you write clean, idiomatic Nanyx code.

# Why use it

- Keeps style consistent across contributors
- Reduces formatting-only diffs in reviews
- Works well with linter guidance and editor tooling

# Usage modes

The formatter is available as a command within the Nanyx CLI. You can run it in different modes:

## Check

Check mode verifies that files are already formatted according to the standard style. It exits with a non-zero status if any files would be changed by formatting.

```bash
# Check all files in the current working directory
nanyx format --check

# Check all files in the `src/` directory
nanyx format --check src/

# Check specific files
nanyx format --check src/file1.nyx src/file2.nyx
```

## Write

Write mode applies formatting changes to the specified files. This is the default mode if no flags are provided.

```bash
# Format all files in the current working directory
nanyx format
# or
nanyx format --write

# Format all files in the `src/` directory
nanyx format src/

# Format specific files
nanyx format src/file1.nyx src/file2.nyx
```

## Files and exit codes

A directory means every `.nyx` file under it, leaving out `bin`, `obj`, `node_modules` and hidden directories. Each file keeps its line endings.

A file that does not parse is left as it is and its syntax error is reported. The formatter also parses its own output before writing it, and leaves a file alone if the result would not mean exactly the same as before.

`nanyx format` exits with `0` when it has nothing to report, `1` when `--check` finds a file that is not formatted, and `2` when a file could not be formatted.

# Imports

Imports in a row go in one bracketed `import (...)`, and their names are sorted: standard library modules (`nanyx/...`) first, then other packages' modules, then imports by path, each alphabetical. A blank line keeps groups of imports apart, and a comment on a line of its own (or a `---` block comment) starts a new group inside the brackets. A comment at the end of an import's line moves with it.

```nanyx
-- before
import nanyx/text
import "./tokens.nyx"
import nanyx/math -- for sqrt

-- after
import (
  nanyx/math -- for sqrt
  nanyx/text
  "./tokens.nyx"
)
```

# Typical workflow

Format files before committing changes, and enable format-on-save in your editor where possible.

We also recommend adding a `nanyx format --check` step to your CI pipeline to ensure that all committed code adheres to the standard style when trying to merge a PR.

# Examples

```nanyx
-- before
def add={a,b->a+b}

-- after
def add = { a, b -> a + b }
```

```nanyx
-- before
def mapOption: (#some(a) | r), (a -> b) -> (#some(b) | r) = { 
  | #some(a), f -> #some(f(a))
  | other, _ -> other }

-- after
def mapOption
  : (#some(a) | r), (a -> b) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }
```

# Configuration

The formatter has a small set of configuration options that can be set in a `[formatter]` table of the package's `.nyxp` manifest. These options are intentionally limited to keep style consistent across contributors and projects.

```toml
[formatter]
# The maximum line width before wrapping occurs (default: 100)
lineWidth = 100
# The number of spaces to use for indentation (default: 2)
indentSize = 2
```

Each file is formatted with the settings of the nearest `.nyxp` in its directory or a directory above it, so one run can format several packages, each in its own style. With no manifest, or no `[formatter]` table in it, the defaults apply. The table may hold only these two settings, each a positive integer: anything else is an error naming the manifest and the setting, and no file under that manifest is formatted (`nanyx format` exits with 2). The language server reads the same settings for the document it formats, and leaves the document alone if they are not valid.

# Related

See [Official style linter](./official-linter) for style diagnostics that complement formatting.
