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
  package = (name = "acme.web", version = "1.2.0", sources = ["**/*.nyx"])
  dependencies = [(package = "acme.core", path = "../core")]
)
```

A manifest holds only data: records, lists, tuples, strings, numbers, `true` and `false`, tags such as `#error`, and `--` comments. A name, a call, an operator or any other code in it is an error, so a manifest can always be read without running anything. Its fields are `package`, `dependencies`, `linters`, `host` and `formatter`; any other is an error. `package` must have a `name`, lower case with words separated by dots, and a `version`, a semantic version. A package to publish also gives its `repository`, the GitHub repository whose releases are its versions, as `repository = "acme/web"`, and its `nanyx`, the oldest compiler it builds with, as `nanyx = "0.52"`, which `nanyx new` writes for the compiler that makes the package. The package index records it, so a compiler older than that never chooses the version, and one that is given the package anyway refuses it, at the field (`NYX0052`). A package that builds with one compiler only, as a host does, since it uses the compiler's internals, writes `=` and that compiler's version, `nanyx = "=0.52.0"`, and no other compiler chooses it or builds it. A host declares itself one in its `package`'s `host`, and a package that hosts are written on says `hostLibrary = true` ([Writing a host](../advanced/hosts#writing-a-host), [Host libraries](../advanced/hosts#host-libraries)).

A manifest written in TOML, starting `[package]` or `name = "..."`, is an error that says so (`NYX0034`). While a package's manifest has errors, checking or running any of its files reports them, at the manifest, rather than checking the file on its own.

The editor checks a manifest as you type it, and the formatter formats it as it does any Nanyx. For scripts and other tools that read a manifest, [`nanyx manifest --json`](../standard-tools/command-line#nanyx-manifest) checks it and prints it as JSON: a record is an object, a list or a tuple is an array, and a tag is a string, `"#error"`.

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

## Test files

A package can keep [tests](./attributes#test) in files of their own. The `tests` field of `package` selects them, as `sources` selects the rest:

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0", sources = ["src/**/*.nyx"], tests = ["tests/**/*.nyx"])
)
```

A test file is part of the package in a test build alone, and every other build leaves it out. Like a source file, it declares a named module, and it can import the package's modules. A file that both `sources` and `tests` select is an error at the manifest (`NYX0023`). With no `tests`, a package has no test files.

## Test packages

The tests of a package can also live in a package of their own, beside it, as a .NET test project sits beside the project it tests. A manifest that gives `tests` and no `sources` is a **test package**'s:

```nanyx fragment
(
  package = (name = "cart.tests", version = "0.1.0", tests = ["tests/**/*.nyx"])
  dependencies = [(package = "cart", path = "../cart")]
)
```

A test package has no source files, rather than every file, as a manifest with no `sources` otherwise has; `sources = []` says the same. It needs no host and exports nothing. Its tests run under the test host, and import what the packages it depends on export, as any dependent does: here `import cart/pricing`, for a `cart` whose `pricing.nyx` starts `export module pricing`. So they see only what a package exports, and a private helper is tested beside its code.

It can depend on any package, an application included. In the test build, the application's `@entry` is an ordinary def, as it is in its own test build, and its modules keep its host, whose prelude they see. `nanyx test cart-tests` runs the tests, and `nanyx check cart-tests` and the editor check them as the test build compiles them; `nanyx run` and `nanyx build` of a test package are an error that says to use `nanyx test` (`NYX0045`). A `sources` whose patterns match no file is still an error (`NYX0042`).

# Package dependencies

Dependencies on other packages are declared in the package manifest's `dependencies` list. Each entry names a package and the directory it is in, relative to the manifest:

```nanyx fragment
(
  package = (name = "acme.app", version = "0.1.0")
  dependencies = [(package = "acme.core", path = "../core"), (package = "acme.web", path = "../web")]
)
```

The directory holds the package's own `.nyxp`, whose `name` must be the one the entry gives. A package the compiler ships, such as `nanyx.cli`, is a package from the index like any other, named at a version: `(package = "nanyx.cli", version = "0.52.0")`. The compiler seeds the version it ships into the package cache, so the compiler's own version needs no download, and a build with no network finds it.

Any other package named by its version, `(package = "acme.web", version = "1.2")`, comes from the [package index](https://packages.nanyx.dev/). `1.2` accepts `1.2.0` and every later `1.x` version; below `1.0.0`, `0.3` accepts only `0.3.x`. The version a build uses is the one `nanyx.lock`, beside the manifest, locks: `nanyx pull` downloads each package the lockfile locks into a cache in your home directory, checking it against the SHA-256 the lockfile records, and a build uses it from there. A locked package that isn't in the cache is an error that says to pull (`NYX0041`). `nanyx pull`, `nanyx install`, `nanyx build` and `nanyx run` choose the versions to lock when the manifest asks for something the lockfile doesn't lock: the newest version that every package's dependencies accept, since a build has one version of each package, keeping each version already locked while it is still accepted. When there is none, the error explains why (`NYX0047`). `nanyx build --locked` refuses to change the lockfile, for CI. Commit `nanyx.lock`, so that every machine builds the same versions. `nanyx pack` packs a package as the `.nyxpkg` a release carries. See [Packages the compiler ships](../advanced/hosts#packages-the-compiler-ships).

Source code imports modules using the dependency's package name:

```nanyx fragment
import (
  acme.core/collections
  acme.web/http
)
```

Only the modules a package declares with `export module` can be imported from another package (`NYX0025`):

```nanyx fragment
export module collections
```

A package can only import modules from packages that it directly depends on (`NYX0026`). Dependencies of dependencies are not implicitly available to source code. A build has one copy of each package: two dependencies that reach the same package in different directories are an error (`NYX0028`), and so is a cycle of packages that depend on each other (`NYX0027`). The dependencies' modules are checked once each and compiled into the same program as the package's own.

## Dependencies on GitHub

A dependency can also be code in a GitHub repository, pinned to a commit: a whole package, whose repository has its `.nyxp` at its root, or a single file, which becomes a module of this package under the name the entry gives it:

```nanyx fragment
dependencies = [
  (package = "acme.web", github = "acme/web", rev = "v1.2.0", commit = "1f8ae8b3c9d04e5f6a7b8c9d0e1f2a3b4c5d6e7f")
  (module = "retry", github = "acme/snippets/retry.nyx", rev = "1f8ae8b3c9d04e5f6a7b8c9d0e1f2a3b4c5d6e7f")
]
```

The package is imported as one on disk is, `import acme.web/http`, and the file by its module name, `import retry`. A reference must be pinned to a full commit SHA, as its `rev` or as a `commit` beside a tag or branch, because what a tag or branch names can move (`NYX0038`); an editor offers to look the commit up and write it in. A file from GitHub declares no `module` name of its own, can't be exported, and imports only the standard library (`NYX0040`). A package with a GitHub reference can be built and run, but not packed or published (`NYX0039`). In a script, `::install retry github:acme/snippets/retry.nyx@<commit>` does the same.

`nanyx pull` fetches what GitHub references name into the package cache, and a build reads it from there: one not fetched yet is an error that says to pull (`NYX0041`).

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

External package visibility is controlled separately, at the module level, by the module's own declaration. A module declared with `export module` is public, and any other is internal to its package:

```nanyx fragment
export module http

import http.internal

export def get = { url: string -> createConnection(url) }
```

A consuming package can import the public modules:

```nanyx fragment
import acme.web/http
```

but cannot directly import other modules in the package.

This allows a package to contain internal modules whose exported definitions are shared between its own modules without making those modules part of its external API. A public module can make some of them part of its API all the same, by re-exporting them: `export http.internal (createConnection)`.

A library, a package whose manifest names no `host` and which has no `@entry`, must export at least one module, since another package could use nothing of it otherwise (`NYX0033`). An application needn't export any. There is no default module: a package's modules are always imported by name, as `acme.web/http`.

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
  host = (package = "nanyx.cli.host", version = "0.52.0")
)
```

`nanyx.cli.host` is the host for a command-line program. The compiler ships it beside itself, so it needs nothing installed, and it brings `println`, `Console`, `Env` and `FileSystem` into scope in every module of the program. A host the compiler doesn't ship is found by its path instead of a version, as a dependency is: `host = (package = "acme.host", path = "../acme.host")`. A package with no `host` is a library: it can't have an `@entry`, and running it is an error. `nanyx run`, `nanyx build` and `nanyx check` take `--host` with a host's name or directory to choose one for a single command instead. A single `.nyx` file runs under `nanyx.cli.host` unless it names another host with a `::host` directive (see [Script mode](#script-mode)). [Hosts](../advanced/hosts) describes what a host provides, and how to write one.

# Building and running a package

In a package's directory (or any directory under it), `nanyx build` checks every source file the manifest selects and writes the program to `out/<package name>.wasm`, and `nanyx run` checks them and runs the package's `@entry`. Both also take the package's directory or manifest:

```text
nanyx run examples/greeter
nanyx build examples/greeter/greeter.nyxp
```

Both are debug builds: a type or name error is reported, but does not stop the program being built or run. The code where it was found reports the error again, and stops the program, if the program reaches it (see [Deferred errors](./error-handling#deferred-errors)). `nanyx build --release` refuses any error.

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
::linter nanyx.lint
::lint unusedBindings #error
::format indentSize 4

import "./utils" as utils
```

Each directive is a name and the words it takes, separated by spaces: `::linter nanyx.lint` and the `::lint unusedBindings #error` under it say what `linters = [(package = "nanyx.lint", version = "0.52.0", settings = (rules = (unusedBindings = #error)))]` says in a manifest. A `::lint` line is for the linter of the nearest `::linter` above it. `::host` and `::install` name the script's host and each package it imports from, as a manifest's `host` and `dependencies` do:

```nanyx fragment
::host nanyx.cli.host
::install acme.util ../util

import acme.util/strings
```

A package's name is followed by where it is found: a directory, which starts with `.` or `/`, relative to the script, or a version, which starts with a digit. A package from the index is named at the exact version the script uses, `::install acme.json 1.2.0`: a script has no lockfile, so that version is its pin, and `nanyx run` downloads it. A package the compiler ships needs neither: it is at the compiler's version. Without `::host`, a script runs under `nanyx.cli.host`. The directives are listed in [Files and projects](../getting-started/files-and-projects). A file in a package cannot have directives: they go in the package's manifest instead.

Script modules and named package modules are distinct. A named module is imported through its package or module identity; an anonymous script module is imported by file path.

# Building packages

A package is the unit that is built, versioned, distributed, and referenced by other packages.

Internally, Nanyx compiles at module granularity. Modules can therefore be checked, cached, and rebuilt independently where their dependencies allow it.

Externally, however, a package is produced and consumed as a single package artifact.

This keeps package boundaries useful for dependency management and distribution without making the whole package the smallest unit of compilation.

## Versions

A package's version is a [semantic version](https://semver.org), and the compiler decides what it must be from what
the package exports, as Elm does: what another package can see is its public API, the exports of each module it
declares with `export module`, with their types. `nanyx pack` records it in the package's `.nyxpkg` as `api.json`, and
`nanyx bump` compares it with the newest version published and writes the version the changes need into the
manifest:

- **Major:** an export removed or renamed, a def's type changed other than made more general, a new context it
  requires, a field or a tag added to or removed from an exported type, a type's parameters changed, or a type made
  protected or private.
- **Minor:** a new export or module, a def's type made more general, or a context it no longer requires.
- **Patch:** no change to the public API.

Below `1.0.0`, a breaking change needs a minor bump, `0.3.1` to `0.4.0`, and any other a patch. A dependant that asks
for `1.2` gets any `1.x` version from `1.2.0` on, so a change the version doesn't announce would break its build. An
inferred type can change by accident, so writing the types of what a module exports keeps its API what you meant.

## Publishing

`nanyx publish` publishes the version a package's manifest gives. It checks that the manifest names the oldest compiler the package builds with, `nanyx`, and that the package builds and is formatted,
and that its version is a big enough bump from the newest version published below it, by the rules above. Then it
packs the package and creates the release `v1.2.0`, with the `.nyxpkg` as its asset, on the package's `repository`.
The [package index](https://packages.nanyx.dev/) records each new release within the hour. For a package's
first version, `publish` also opens the pull request that registers it with the index, which a maintainer merges.

A published version never changes. `nanyx unlist acme.web 1.2.0` opens the pull request that unlists it: once that is
merged, no new resolution chooses it, but a lockfile that locks it keeps building, with a warning that it is unlisted. Both commands sign in to GitHub
with `GITHUB_TOKEN`, or else the GitHub CLI's login. See [`nanyx publish`](../standard-tools/command-line#nanyx-publish).
