---
title: "The nanyx command"
description: "Every command and option of the nanyx CLI: running, checking, building, formatting and managing packages"
order: 0
---

`nanyx` is one program that compiles, runs and formats Nanyx code, manages packages, searches the standard library
and is the language server for editors. `nanyx --help` lists its commands, and `nanyx <command> --help` describes one.

```text
nanyx run [--check-memory | --memory] [--timings] [--host <host>] [<file.nyx | package>] [--] [<arguments>]
nanyx check [--timings] [--host <host>] [<file.nyx | package>]
nanyx build [--out <path>] [--release] [--wat] [--timings] [--host <host>] [<package>]
nanyx wasm <file.nyx> [--out <path>] [--release] [--wat]
nanyx format [--check | --write] [<files or directories>]
nanyx format [--check] --stdin
nanyx new <name> [<directory>]
nanyx migrate [<file.nyx | directory>] [--name <name>]
nanyx pull [<package>]
nanyx install <package> [--path <directory> | --version <version>]
nanyx pack [--out <directory>] [<package>]
nanyx bump [<package>]
nanyx manifest --json [<package>]
nanyx search <query>
nanyx lsp [<options>]
nanyx hello
nanyx --version
```

A _package_ here is a package's directory or its `.nyxp` manifest. Where a command takes one and you give none, it
uses the package the current directory is in: the manifest in that directory or the nearest directory above it. A
path that ends in `.nyx` is a script, run or checked on its own (see [Files and projects](../getting-started/files-and-projects)).

# Running and checking code

## `nanyx run`

`nanyx run` compiles a program and runs it. Given a `.nyx` file, it runs the file as a script; given a package, or
nothing, it checks every source file the manifest's `sources` select and runs the package's `@entry`.

```bash
nanyx run hello.nyx     # a script
nanyx run               # the package the current directory is in
nanyx run ../greeter    # the package in that directory
```

The program's output is the only thing written to standard output. The compiler's diagnostics go to standard error,
and `run` shows only errors and warnings: info and hint diagnostics are for code you are working on, so
[`nanyx check`](#nanyx-check) and the editor show them.

What follows the file or package is the program's own: `nanyx run tool.nyx a b` passes `a` and `b`, which the program
reads with `args()` from [`Env`](../stdlib/env). To pass arguments to the package in the current directory, name it:
`nanyx run . a b`. An argument that starts with `-` would be read as one of `run`'s options, so put `--` before the
program's arguments: `nanyx run tool.nyx -- -v --all` passes `-v` and `--all`. Everything after the first `--` goes
to the program as it is, a second `--` included. The program also sees the process's environment variables, through
`variable`, and when it calls `exit(code)`, `run` exits with that code.

Under the console host, the program reads and writes files through [`FileSystem`](../stdlib/filesystem), in the
current directory and the directories in it, and nowhere else unless `--allow-dir` gives it another directory: a path
outside them is `#error(#permissionDenied)`, whichever user runs it.

`run` is a debug build: a program with type or name errors still runs, and reports each error if it reaches it (see
[Deferred errors](../tour/error-handling#deferred-errors)). An error that can't be deferred, such as a syntax error,
stops it before the program starts.

A program that crashes, reaches a deferred error or stops at a runtime error, such as division by zero, writes why to
standard error with the stack under it: each function it was in, with the file and line it had reached, innermost
first (see [Crash stacks](../compiler-internals/crash-stacks)). It exits with 1.

A package runs under the host its manifest's `host` names. A package without a `host` is a library, which can't be
run. A script runs under the console host, `nanyx.console`, unless a `::host` directive names another.

A program whose host answers HTTP requests, as the HTTP host `nanyx.http` does, is served rather than run once: `run`
listens on `http://localhost:<port>/`, the port the host's `port` setting gives (3000 by default), says so on standard
error, and answers each request with a fresh instance of the program until Ctrl+C. A request whose handler stops the
program is answered with a 500, and the reason is written to standard error. See the
[HTTP server cookbook](../cookbook/http-server).

| Option | What it does |
|--------|--------------|
| `--host <host>` | Runs the package's `@entry` under another host, in place of its manifest's: a host package the compiler ships, `--host nanyx.console`, or the directory of one, `--host ../acme.host`. A host the compiler doesn't ship, or a directory whose manifest doesn't declare a host, is an error. A script names its host with `::host`, so `--host` with a `.nyx` file is an error. |
| `--allow-dir <directory>` | Lets the program read and write `<directory>`, and what is in it, as well as the current directory. The program reaches it by its absolute path: `nanyx run --allow-dir ../data tool.nyx`, run in `/home/ada/tool`, lets `tool.nyx` read `path.of("/home/ada/data/input.txt")`. A directory that isn't there is an error. |
| `--check-memory` | Checks the program's memory as it runs: it stops with exit code 70 at a block freed twice or an object used after it was freed, and fails with exit code 1 if the program finishes with memory it never freed. |
| `--memory` | Checks the program's memory as `--check-memory` does, and once the program stops, however it stops, writes a report to standard error (see below). |
| `--timings` | Writes how long each module took to check (see [Timings](#timings)). |

The report of `--memory` says how many blocks of memory the program allocated and freed, how many were still in use
when it stopped, how many objects it built in the memory of one it had finished with rather than allocating (see
[Reference counting](../foundations/reference-counting)), how many bytes it allocated in all, the most it had in use
at once, and the highest address its heap reached, with the memory's size in 64 KiB pages:

```text
Memory
  allocations        1,618
  frees              1,618
  live at exit       0
  reused in place    392
  bytes allocated    31,408
  peak live bytes    824
  peak heap          9,260 bytes (1 page)
```

## `nanyx check`

`nanyx check` checks a script, or every source file of a package, and writes nothing. Unlike `run`, it fails on any
error, including one a debug build would defer, and it shows every diagnostic, info and hints included.

For a script, it also prints the type of each top-level definition the file declares, in the order they are written,
as hover in the editor writes them, with type variables `a`, `b`, …:

```text
Phase: Typed
No diagnostics.
Types:
greet : string -> string
twice : [ToString(a)] a -> string
```

For a package, it prints the diagnostics and then `Checked greeter (3 files).`. A file that belongs to a package is
checked as part of its package. If the package's manifest has errors, `check` reports them, at the manifest, and
nothing else.

`check` takes `--timings` and `--host`, as `run` does.

## `nanyx build`

`nanyx build` checks every source file of a package, with the linters its manifest turns on, and writes the program
of its `@entry` as a WebAssembly module, to `out/<package name>.wasm` beside the manifest: the binary format, which
browsers, Wasmtime and other WebAssembly runtimes load. A package with no `@entry` is only checked.

| Option | What it does |
|--------|--------------|
| `--out <path>`, `-o <path>` | Writes the program to another file, making its directory if need be. |
| `--release` | Refuses any error, and leaves out every [`dbg`](../stdlib/console#dbg): `dbg(x)` compiles to `x`. Without it, a build is a debug build: type and name errors are deferred, and the program is written all the same. |
| `--wat` | Writes WebAssembly text, to `out/<package name>.wat`, rather than the binary format: the same program, for reading. |
| `--timings`, `--host <host>` | As for `run`. |

## `nanyx wasm`

`nanyx wasm file.nyx` compiles a script to a WebAssembly module, written beside it as `file.wasm`, or to `--out <path>`
(`-o`). With `--wat` it writes WebAssembly text, `file.wat`, instead. Like `build`, it is a debug build unless you pass
`--release`.

## Exit codes

Every command exits with the same codes:

- **0** when it succeeds. A program that `run` runs exits with its own status instead.
- **1** when the code has errors, the package's manifest can't be found or read, or the program fails.
- **2** when the command is used wrongly: more than one path where it takes one, a file or manifest that does not
  exist, or an option's value it doesn't take. `format` also exits with 2 for a file it could not format.

# What the compiler shows while it works

While `run`, `build`, `check` or `wasm` compiles, a line on standard error names the file the compiler is on and how
long the compile has taken: `Compiling src/main.nyx… 4.7s`. While it checks a module that file imports, it names that
too: `Compiling src/main.nyx › nanyx/list… 1.2s`. When the compile finishes, the line becomes
`Compiled greeter (12 files) in 4.7s`, or `Compiled main.nyx in 1.2s` for a script, and the diagnostics follow.

This line is shown only when standard error is a terminal. A pipe, a log file and CI see nothing of it, and the
command's other output is the same either way.

## Timings

`--timings`, on `run`, `build` and `check`, also writes how long each module's check took, as a tree, after the
compile's last line:

```text
src/greetings.nyx (0.05s of 0.21s)
└── src/format.nyx (0.16s)
    └── nanyx/prelude (cached)
src/main.nyx (0.20s of 0.25s)
├── nanyx/prelude (cached)
├── nanyx/text (cached)
├── src/format.nyx (cached)
└── src/greetings.nyx (cached)
Emitted the program in 0.031s
```

Each line is one module's check, as `name (self of total)`. A module's imports are checked as part of its own check,
so they are under it: the total includes them, and the self time is what is left, the module's own parsing and type
checking. Where the two are the same, the line has one time. Each module is checked once per build, and the standard
library is checked when the compiler is built, so a module needed again, or one of the library's, is `(cached)`. The
last line is how long the program took to write out as WebAssembly.

The tree is written whether or not standard error is a terminal. On a terminal, it is also drawn while the compile
runs, in place of the `Compiling …` line: each check under way shows how long it has taken so far, `(0.1s…)`, and
when it is taller than the terminal, the lines furthest from the check under way are folded into one, `… 12 more`.

## The run's box

`nanyx run` writes the compiler's output and the program's to the same terminal. When the compiler has more to say
than its `Compiled …` line, `run` puts what it says in an open-sided box, and the program's output follows it:

```text
╭─ nanyx run emails ───────────────────
│ Compiled emails (3 files) in 1.9s
│ src/main.nyx:12:5: error NYX0102: Unknown function 'lookup'
│    |
│ 12 |     lookup(id)
│    |     ^^^^^^
│ Running with 1 deferred error
╰─ output ─────────────────────────────
invalid user id
23
```

The box is drawn only on a terminal. A run with nothing to report but `Compiled … in` gets no box, and neither does a
pipe or a log, which sees the compiler's lines as they are.

# Formatting

`nanyx format` lays out Nanyx code in the standard style. With no path, it formats every `.nyx` file under the
current directory; a directory means every `.nyx` file under it.

| Option | What it does |
|--------|--------------|
| `--write` | Rewrites each file that is not formatted. This is the default. |
| `--check` | Writes nothing: lists each file that is not formatted, and exits with 1 if there are any. |
| `--stdin` | Formats standard input and writes the result to standard output, for an editor to format a buffer it has not saved. |

See [Formatter](./formatter) for the style, the settings and what happens to a file that can't be formatted.

# Packages

## `nanyx new`

`nanyx new acme.demo` makes a package in a new directory, named for the last part of the package's name, `demo`,
unless you give another: `nanyx new acme.demo tools/demo`. The directory must be new or empty. The package has a
manifest, `acme.demo.nyxp`, with the official linter on and the console host, both at the compiler's version, and
`src/main.nyx`, whose `@entry` prints "Hello, World!". A package's name is lower case, with words separated by dots.

## `nanyx migrate`

`nanyx migrate` turns a script, or every `.nyx` file in a directory (the current one by default), into a package:

- It writes a manifest, `<name>.nyxp`, beside the files, named by `--name` or by the directory, at version `0.1.0`,
  whose `sources` select the files it migrated.
- Each `::` [directive](../getting-started/files-and-projects#directives) becomes the manifest entry it stands for,
  and is removed from its file, with the `#!` line: `::dependency acme.util ../util` becomes
  `(package = "acme.util", path = "../util")` in `dependencies`, and each `::lint` goes into the `settings` of the
  linter above it.
- A script with top-level statements becomes `module main`, and its statements move into an `@entry` def, with the
  console host unless a `::host` names another. Any other file becomes a module named for the file, and the package's
  imports of a file by its path become imports of its module.

The files are written formatted. `migrate` refuses a directory that is already a package, more than one script with
top-level statements (migrate those one at a time), a directive written wrong, and two directives that set one thing
differently.

## `nanyx install`

`nanyx install` adds a dependency to the manifest of the package the current directory is in:

```bash
nanyx install acme.util --path ../util   # the package in that directory
nanyx install nanyx.console.core         # a package the compiler ships, at the compiler's version
```

The directory must hold a package of the name you give, and a package already listed is an error. Choosing the version
to lock in `nanyx.lock` isn't supported yet, so `--version` writes the entry and then fails, saying so.

## `nanyx pull`

`nanyx pull` downloads into the package cache each package `nanyx.lock` locks that isn't there yet, checking each
against the SHA-256 the lockfile records, then checks that each dependency of a package is there, as a build would, and
lists them: `acme.util 0.3.0 (../util)`, `acme.web 1.2.0 (downloaded)`. A dependency named by its version that the
lockfile doesn't lock can't be had yet, since choosing a version isn't supported yet, so `pull` fails for one.

It fetches each dependency on GitHub, a package or a file, at the commit it is pinned to, into the cache too. Set
`GITHUB_TOKEN` to a token of yours to have GitHub count its requests against your account.

The cache is `.nanyx/packages` in your home directory, or the directory `NANYX_PACKAGES` names. Packages are
downloaded from the [package index](https://nanyx-lang.github.io/packages/), or from the one `NANYX_INDEX` names: an
address, or a directory on disk.

## `nanyx pack`

`nanyx pack` packs a package as a `.nyxpkg`, the file a release of it carries, and prints its SHA-256:

```bash
nanyx pack                    # writes acme.util-0.3.0.nyxpkg beside the manifest
nanyx pack --out dist ../util
```

A `.nyxpkg` is a zip of the package's manifest, its source files and its `api.json`, which records what each module it
exports with `export module` exports, with its type, so that the next version can be compared with it. Packing the same
files twice makes the same file, and a package that doesn't compile can't be packed. A package with a dependency found by its path, or on GitHub, can't be packed, since that dependency isn't there
once the package is unpacked elsewhere.

## `nanyx bump`

`nanyx bump` sets a package's version to the one the changes to its public API need, compared with the newest version
of it published, and lists the changes:

```text
major: strings's def whisper was removed, or is no longer exported
acme.util 1.0.0 -> 2.0.0, after 1.0.0: written to util.nyxp
```

An export removed or changed needs a major bump, one added, or a type made more general, a minor bump, and no change
to the public API a patch. Below `1.0.0`, a breaking change needs a minor bump and any other a patch. A version you
wrote already that is big enough stays, and with no version published there is nothing to compare, so the version
stays as it is. See [Packages and projects](../tour/packages#versions).

## `nanyx manifest`

`nanyx manifest --json` checks a package's manifest and prints it as JSON, for scripts and tools outside the
compiler: a record is an object, a list an array, and a tag such as `#error` the string `"#error"`. A manifest with
problems prints them, as `check` does, and no JSON. `--json` is required, since JSON is the only form so far.

# The standard library

`nanyx search` searches the standard library by name, by type, or both, and prints what matches, best first, as the
editor's [library search](./vs-code-extension#searching-the-library) does:

```bash
nanyx search trim
nanyx search "bool -> string"
nanyx search "trim: string -> string"
```

A name finds the names that contain it. A type, written as a definition's annotation writes it, finds the functions
and values whose types can be made equal to it: its type variables stand for any type, and a function's inputs may
come in any order. Each match is printed with its signature, the import it needs, if any, and the contexts it still
needs at the types it matched, if any:

```text
$ nanyx search trim
def string.trim: string -> string  (import nanyx/text)
def string.trimEnd: string -> string  (import nanyx/text)
def string.trimStart: string -> string  (import nanyx/text)
```

It exits with 1 when nothing matches, and with 2 when the query is neither a name nor a type.

# Editors

`nanyx lsp` runs the language server over standard input and output. The [VS Code extension](./vs-code-extension)
starts it for you, passing these options from its settings; any other editor with an LSP client can start it as
`nanyx lsp` (see [Installation](../getting-started/installation#editor-support)).

| Option | What it does |
|--------|--------------|
| `--ascii` | Writes types in ASCII only: type variables `a`, `b`, … rather than `α`, `β`, …, and a record's other fields as `...` rather than `…`. |
| `--top-level-type-hints <lens \| inlay \| off>` | Where to show the inferred types of top-level definitions: in a lens above each (the default), as an inlay hint after its name, or not at all. |
| `--no-local-type-hints` | Shows no inlay hints for local definitions' types. |
| `--no-parameter-type-hints` | Shows no inlay hints for lambda parameters' types. |
| `--no-pattern-variable-type-hints` | Shows no inlay hints for pattern variables' types. |
| `--no-format-hints` | Doesn't mark the lines `nanyx format` would change. |
| `--timings` | Logs how long each module took to check, with each compile, as [Timings](#timings) shows it. |
| `--stdio` | Accepted and ignored: standard input and output are the only transport, but LSP clients often pass it. |

# Checking the installation

`nanyx --version` prints the compiler's version, such as `0.28.0`. `nanyx hello` compiles and runs a "Hello, World!"
program built into the CLI, to check that the compiler and the runtime work.

# For working on the compiler

These are for people working on the compiler and the standard library, and are not needed to write Nanyx:

- `nanyx parse file.nyx` prints the syntax tree of a file, without checking it.
- `--no-inline`, on `run` and `build`, compiles every call as a call. Inlining changes no behaviour, so a program
  that behaves differently with it points at a bug in the compiler.
- `nanyx stdlib-docs <directory>` writes the reference on each page of the docs site's
  [Standard Library](../stdlib/standard-library) section from the library's doc comments, and with `--check` lists
  the pages that are out of date and exits with 1.
