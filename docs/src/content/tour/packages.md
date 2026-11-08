---
title: "Packages and projects"
description: "Organizing modules for distribution and reuse"
order: 12.1
---

Packages group related modules into units that can be built, distributed, and referenced by other packages. Projects group packages that are developed together.

# Packages

A package contains one or more named modules and is described by a `.nyxpack` manifest.

For example, a package named `acme.web` might have the following structure:

```text
acme.web.nyxpack
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

```nanyx
import (
  acme.web/http
  acme.web/json.deserialization
)
```

The `/` separates the package name from the module name. Dots within a module name are part of that module's name and can be used to organize modules hierarchically.

# Package manifests

Every package has a `.nyxpack` manifest written in TOML.

The manifest describes the package and the source files and dependencies that make it up.

```toml
[package]
name = "acme.web"
version = "1.2.0"
sources = ["**/*.nyx"]
public-modules = [
  "http",
  "json"
]

[dependencies]
acme.core = "2.1"
```

The exact set of available package metadata may grow over time, but package manifests are declarative documents rather than Nanyx programs.

## Source files

The package manifest determines which Nanyx source files belong to the package:

```toml
[package]
sources = [
  "src/**/*.nyx",
  "generated/**/*.nyx"
]
```

If `sources` is omitted, it defaults to:

```text
**/*.nyx
```

All source files included in a package must contain named modules.

An anonymous module is therefore a compilation error when encountered during a package build. Anonymous modules are intended for script mode and file-path imports rather than inclusion in normal packages.

# Package dependencies

Dependencies on other packages are declared in the package manifest:

```toml
[dependencies]
acme.core = "2.1"
nanyx.web = "1.4"
```

Source code imports modules using the dependency's package name:

```nanyx
import (
  acme.core/collections
  nanyx.web/http
)
```

Versions are resolved by the package tooling and do not appear in Nanyx import expressions.

A package can only import modules from packages that it directly depends on. Dependencies of dependencies are not implicitly available to source code.

# Project references

Packages being developed together can reference each other using project references.

```toml
[project-references]
acme.shared = "../shared"
```

A project reference uses the referenced package's declared package identity. Source code therefore imports it in exactly the same way as any other dependency:

```nanyx
import acme.shared/models
```

Whether `acme.shared` comes from a local project reference or an external package dependency does not change the source code that consumes it.

Project references are only meaningful when packages are being built in the context of a project. A standalone package cannot use a project reference to reach an arbitrary local package.

# Public modules

`export` controls visibility between modules:

```nanyx
module http.internal

export def createConnection = ...
def validateConnection = ...
```

Here, `createConnection` is available to other modules in the same package, while `validateConnection` remains private to `http.internal`.

External package visibility is controlled separately, at the module level, by the package manifest:

```toml
[package]
name = "acme.web"

public-modules = [
  "http",
  "json"
]
```

A consuming package can import the public modules:

```nanyx
import acme.web/http
```

but cannot directly import other modules in the package.

This allows a package to contain internal modules whose exported definitions are shared between its own modules without making those modules part of its external API.

Re-exporting can be used to build a public module from definitions implemented across several internal modules.

# Entrypoints

A package may contain an entrypoint using the `@entry` attribute in its source code:

```nanyx
module main

@entry
def main = {
  ...
}
```

A package may contain at most one `@entry`. Declaring more than one is a compilation error.

Packages without an entrypoint can still be built and consumed normally; they simply cannot be run directly.

The entrypoint is declared entirely in source code and does not need to be repeated in the package manifest.

# Building and running a package

In a package's directory (or any directory under it), `nanyx build` checks every source file the manifest selects and writes the program to `out/<package name>.wat`, and `nanyx run` checks them and runs the package's `@entry`. Both also take the package's directory or manifest:

```text
nanyx run examples/greeter
nanyx build examples/greeter/greeter.nyxpack
```

The analyzers that check a package are the ones its manifest names; see [Analyzers](../advanced/analyzers).

# Projects

A project is an optional grouping of packages that are developed together. It is described by a `.nyxproj` TOML document.

For example:

```text
myProject.nyxproj
src/
  app/
    app.nyxpack
    ...
  shared/
    shared.nyxpack
    ...
  domain/
    domain.nyxpack
    ...
```

A project manifest lists its constituent packages:

```toml
[project]
packages = [
  "src/app",
  "src/shared",
  "src/domain"
]
```

Packages do not need to belong to a project. A package without a project can be restored, built, and distributed independently.

Projects primarily exist to describe packages that should be treated as a single development workspace and to make project references between them possible.

## Running a project

A project can optionally nominate one of its packages as its entry package:

```toml
[project]
packages = [
  "src/app",
  "src/shared"
]

entrypoint = "src/app"
```

Running the project runs the `@entry` declared by that package.

If the selected package does not contain an `@entry`, the project cannot be run.

# Script mode

A Nanyx program does not need a package or project manifest in order to run.

When Nanyx is invoked directly on an anonymous source file, it runs in script mode:

```text
nanyx run hello.nyx
```

The script can import other anonymous modules by file path:

```nanyx
import "./utils" as utils
```

For script mode, Nanyx synthesizes a package description in memory rather than requiring a `.nyxpack` file. A script can add to that description with `#:` directives at the top of the file, each saying what one manifest entry would:

```nanyx
#:analyzer default.unusedBindings error
#:formatter indentSize 4

import "./utils" as utils
```

`#:analyzer` and `#:formatter` stand for the `[analyzers]` and `[formatter]` tables, and `#:package <name> <version>` will stand for `[dependencies]` once dependencies are supported. The directives are listed in [Files and projects](../getting-started/files-and-projects). A file in a package cannot have directives: they go in the package's manifest instead.

Script modules and named package modules are distinct. A named module is imported through its package or module identity; an anonymous script module is imported by file path.

# Building packages

A package is the unit that is built, versioned, distributed, and referenced by other packages.

Internally, Nanyx compiles at module granularity. Modules can therefore be checked, cached, and rebuilt independently where their dependencies allow it.

Externally, however, a package is produced and consumed as a single package artifact.

This keeps package boundaries useful for dependency management and distribution without making the whole package the smallest unit of compilation.