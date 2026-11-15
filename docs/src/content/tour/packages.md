---
title: "Packages and projects"
description: "Organizing modules for distribution and reuse"
order: 12.1
---

Packages group related modules into units that can be built, distributed, and referenced by other packages. Projects group packages that are developed together.

# Packages

A package contains one or more named modules and is described by a `.nyxp` manifest.

For example, a package named `acme.web` might have the following structure:

```text
acme.web.nyxp
http.nyx
json.nyx
json/
  deserialization.nyx
```

Its source files might declare these modules:

```nanyx
module http
```

```nanyx
module json
```

```nanyx
module json.deserialization
```

Code in another package can import them using the package name followed by `/` and the module name:

```nanyx fragment
import (
  acme.web/http
  acme.web/json.deserialization
)
```

The `/` separates the package name from the module name. Dots within a module name are part of that module's name and can be used to organize modules hierarchically.

Inside the package, a module is imported by its name alone, or with the package's name as well:

```nanyx fragment
import json.deserialization
import acme.web/http
```

A module is found by the name its file declares, not by where the file is: `json.deserialization` could as well be declared in `src/parsing.nyx`. Two files of a package cannot declare the same module.

# Package manifests

Every package has a `.nyxp` manifest: one Nanyx record, written as data.

The manifest describes the package and the source files and dependencies that make it up.

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0", sources = ["**/*.nyx"], publicModules = ["http", "json"])
  dependencies = [(package = "acme.core", path = "../core")]
)
```

A manifest holds only data: records, lists, tuples, strings, numbers, `true` and `false`, tags such as `#error`, and `--` comments. A name, a call, an operator or any other code in it is an error, so a manifest can always be read without running anything. Its fields are `package`, `dependencies`, `linters`, `host` and `formatter`; any other is an error. `package` must have a `name`, lower case with words separated by dots, and a `version`, a semantic version.

## Source files

The package manifest determines which Nanyx source files belong to the package:

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0", sources = ["src/**/*.nyx", "generated/**/*.nyx"])
)
```

If `sources` is omitted, it defaults to:

```text
**/*.nyx
```

All source files included in a package must contain named modules.

An anonymous module is therefore a compilation error when encountered during a package build, and so is a source file with top-level statements: a package's code runs from its `@entry`. Anonymous modules are intended for script mode and file-path imports rather than inclusion in normal packages.

# Package dependencies

Dependencies on other packages are declared in the package manifest's `dependencies` list. Each entry names a package and the directory it is in, relative to the manifest:

```nanyx fragment
(
  package = (name = "acme.app", version = "0.1.0")
  dependencies = [(package = "acme.core", path = "../core"), (package = "acme.web", path = "../web")]
)
```

The directory holds the package's own `.nyxp`, whose `name` must be the one the entry gives. Packages can't be downloaded yet, so an entry with a `version` instead of a `path` is an error (`NYX0024`).

Source code imports modules using the dependency's package name:

```nanyx fragment
import (
  acme.core/collections
  acme.web/http
)
```

Only the modules a package lists in its `publicModules` can be imported from another package (`NYX0025`):

```nanyx fragment
(package = (name = "acme.core", version = "2.1.0", publicModules = ["collections"]))
```

A package can only import modules from packages that it directly depends on (`NYX0026`). Dependencies of dependencies are not implicitly available to source code. A build has one copy of each package: two dependencies that reach the same package in different directories are an error (`NYX0028`), and so is a cycle of packages that depend on each other (`NYX0027`). The dependencies' modules are checked once each and compiled into the same program as the package's own.

## Packages developed together

Packages that are developed together depend on each other by path, as above. Any package can name another in a directory beside it; no project is needed. The package is imported by its own name, `acme.shared/models`, so the code that uses it stays the same when the dependency later comes from a downloaded package instead. [Projects](#projects) are planned as a way to group such packages and build them together.

# Public modules

`export` controls visibility between modules:

```nanyx fragment
module http.internal

export def createConnection = ...
def validateConnection = ...
```

Here, `createConnection` is available to other modules in the same package, while `validateConnection` remains private to `http.internal`.

External package visibility is controlled separately, at the module level, by the package manifest:

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0", publicModules = ["http", "json"])
)
```

A consuming package can import the public modules:

```nanyx fragment
import acme.web/http
```

but cannot directly import other modules in the package.

This allows a package to contain internal modules whose exported definitions are shared between its own modules without making those modules part of its external API.

Re-exporting can be used to build a public module from definitions implemented across several internal modules.

# Entrypoints

A package may contain an entrypoint using the `@entry` attribute in its source code:

```nanyx fragment
module main

@entry
def main = {
  ...
}
```

A package may contain at most one `@entry`. Declaring more than one is a compilation error.

Packages without an entrypoint can still be built and consumed normally; they simply cannot be run directly.

The entrypoint is declared entirely in source code and does not need to be repeated in the package manifest.

The entrypoint runs under the package's host, which loads the contexts it may use, such as `Console`. The manifest's `host` names the host's package, and the version of it to use:

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0")
  host = (package = "nanyx.console", version = "0.28.0")
)
```

`nanyx.console` is the host for a command-line program, whose module is `nanyx.console/console.host`. The compiler ships it beside itself, so it needs nothing installed. A package with no `host` runs under it too. `nanyx run`, `nanyx build` and `nanyx check` take `--host <module>` to choose one for a single command instead. The hosts the compiler ships are the only ones yet, and a single `.nyx` file always runs under `nanyx.console/console.host`.

# Building and running a package

In a package's directory (or any directory under it), `nanyx build` checks every source file the manifest selects and writes the program to `out/<package name>.wat`, and `nanyx run` checks them and runs the package's `@entry`. Both also take the package's directory or manifest:

```text
nanyx run examples/greeter
nanyx build examples/greeter/greeter.nyxp
```

Both are debug builds: a type or name error is reported, but does not stop the program being built or run. The code where it was found reports the error again, and stops the program, if the program reaches it. `nanyx build --release` refuses any error.

`nanyx check` does the same checks as `nanyx build`, but writes nothing, and any error fails it. It takes the package's directory or manifest too, or a single `.nyx` file.

`nanyx new acme.web` makes a new package in a directory called `web` (the name's last segment), or in the directory you give after the name: an `acme.web.nyxp` manifest with the official linter turned on, and a `src/main.nyx` whose `@entry` prints a greeting. The directory must be new or empty.

The linters that check a package are the ones its manifest names; see [Linters](../advanced/linters).

# Projects

Projects are planned: a `.nyxproj` file that groups packages developed together, so they can be built, checked and run as one workspace. They aren't built yet, and their file's format isn't settled. Packages will never need to belong to one: a package on its own can already be built, and can depend on others by path.

# Script mode

A Nanyx program does not need a package or project manifest in order to run.

When Nanyx is invoked directly on an anonymous source file, it runs in script mode:

```text
nanyx run hello.nyx
```

The script can import other anonymous modules by file path:

```nanyx fragment
import "./utils" as utils
```

For script mode, Nanyx synthesizes a package description in memory rather than requiring a `.nyxp` file. A script can add to that description with `::` directives at the top of the file, each saying what one manifest entry would:

```nanyx fragment
::linters.default.unusedBindings = "error"
::formatter.indentSize = 4

import "./utils" as utils
```

Each directive is a dotted key, with `= value` when it sets a value: `::linters.default.unusedBindings = "error"` says what `linters = [(package = "nanyx.lint", version = "0.28.0", settings = (rules = (unusedBindings = #error)))]` says in a manifest. `::dependencies.<name> = "<version>"` names a package to download, which isn't supported yet, so it is an error for now. The directives are listed in [Files and projects](../getting-started/files-and-projects). A file in a package cannot have directives: they go in the package's manifest instead.

Script modules and named package modules are distinct. A named module is imported through its package or module identity; an anonymous script module is imported by file path.

# Building packages

A package is the unit that is built, versioned, distributed, and referenced by other packages.

Internally, Nanyx compiles at module granularity. Modules can therefore be checked, cached, and rebuilt independently where their dependencies allow it.

Externally, however, a package is produced and consumed as a single package artifact.

This keeps package boundaries useful for dependency management and distribution without making the whole package the smallest unit of compilation.