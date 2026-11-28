---
title: "The nanyx command"
description: "Every command and option of the nanyx CLI: running, checking, building, formatting and managing packages"
order: 0
---

`nanyx` is one program that compiles, runs and formats Nanyx code, manages packages, searches the standard library
and is the language server for editors. `nanyx --help` lists its commands, and `nanyx <command> --help` describes one.

```text
nanyx run [--check-memory | --memory | --profile <file>] [--timings] [--host <host>] [--dbg-to <file> [--show-values]] [--files-in-memory] [<file.nyx | package>] [--] [<arguments>]
nanyx check [--timings] [--host <host>] [<file.nyx | package>]
nanyx build [--out <path>] [--release] [--core] [--wat] [--locked] [--timings] [--host <host>] [<package>]
nanyx wasm <file.nyx> [--out <path>] [--release] [--wat]
nanyx format [--check | --write] [<files or directories>]
nanyx format [--check] --stdin
nanyx new <name> [<directory>]
nanyx migrate [<file.nyx | directory>] [--name <name>]
nanyx pull [<package> | <file.nyx>]
nanyx install <package> [--path <directory> | --version <version>]
nanyx pack [--out <directory>] [<package>]
nanyx bump [<package>]
nanyx api-check <older.nyxpkg> <newer.nyxpkg>
nanyx publish [<package>]
nanyx unlist <package> <version>
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
nothing, it checks every source file the manifest's `sources` select and runs the package's `@entry`. A test file, one
the manifest's `tests` selects, has no program, and running one is an error that says to use `nanyx test`
([NYX0045](./errors#nyx0045)).

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
outside them is `#permissionDenied`, whichever user runs it.

`run` is a debug build: a program with type or name errors still runs, and reports each error if it reaches it (see
[Deferred errors](../tour/error-handling#deferred-errors)). An error that can't be deferred, such as a syntax error,
stops it before the program starts.

A program that crashes, reaches a deferred error or stops at a runtime error, such as division by zero, writes why to
standard error with the stack under it: each function it was in, with the file and line it had reached, innermost
first (see [Crash stacks](../deep-dives/crash-stacks)). It exits with 1.

A package runs under the host its manifest's `host` names. A package without a `host` is a library, which can't be
run. A script runs under the console host, `nanyx.cli.host`, unless a `::host` directive names another.

A program whose host answers HTTP requests, as the HTTP host `nanyx.http.host` does, is served rather than run once: `run`
listens on `http://localhost:<port>/`, the port the host's `port` setting gives (3000 by default), says so on standard
error, and answers each request with a fresh instance of the program until Ctrl+C. A request whose handler stops the
program is answered with a 500, and the reason is written to standard error. See the
[HTTP server cookbook](../cookbook/http-server).

| Option | What it does |
|--------|--------------|
| `--host <host>` | Runs the package's `@entry` under another host, in place of its manifest's: a host package the compiler ships, `--host nanyx.cli.host`, or the directory of one, `--host ../acme.host`. A host the compiler doesn't ship, or a directory whose manifest doesn't declare a host, is an error. A script names its host with `::host`, so `--host` with a `.nyx` file is an error. |
| `--allow-dir <directory>` | Lets the program read and write `<directory>`, and what is in it, as well as the current directory. The program reaches it by its absolute path: `nanyx run --allow-dir ../data tool.nyx`, run in `/home/ada/tool`, lets `tool.nyx` read `path.of("/home/ada/data/input.txt")`. A directory that isn't there is an error. |
| `--check-memory` | Checks the program's memory as it runs: it stops with exit code 70 at a block freed twice or an object used after it was freed, and fails with exit code 1 if the program finishes with memory it never freed. |
| `--memory` | Checks the program's memory as `--check-memory` does, and once the program stops, however it stops, writes a report to standard error (see below). |
| `--files-in-memory` | Runs the program with its files in memory, over the real ones: it reads a real file until it writes, appends to or deletes it, and then what it left; a directory's listing has what it made and not what it deleted; and nothing it does reaches the disk (see [`filesystem.overlay`](../stdlib/filesystem)). The program is built from its host's root for this, the console host's `cli.host.memory` ([Writing a host](../advanced/hosts#writing-a-host)), and a program built without the flag is unchanged. A host with no such root is an error. The editor's live mode runs a program so. |
| `--profile <file>` | Samples the run with Wasmtime's guest profiler, every 10 ms, and writes the profile to `<file>` in the Firefox profiler's format, to open at [profiler.firefox.com](https://profiler.firefox.com). Its functions have the names the program gives them, and the file and line each is defined on. The run is of the code `run` always runs, which has no [debug probes](../deep-dives/debug-probes). It can't be given with `--check-memory` or `--memory`, nor for a program that is served. |
| `--dbg-to <file>` | Writes each [`dbg`](../stdlib/console#dbg) the program reaches to `<file>`, made anew, and none to standard error: one JSON object a line, written as it is reached, with its `kind`, `dbg`, its `file`, a full path, its `line` and `column`, from 1, its `value` as `dbg` writes it, and its `seq`, counted from 0. It is for an editor, which reads the file as it grows and shows each value where its `dbg` is written, as the [VS Code extension](./vs-code-extension#dbg-values) does. A served program writes its `dbg`s to its log, with the request, whether or not it is given. |
| `--show-values` | With `--dbg-to`, writes to its file each value the program's own code binds or gives too, a line whose `kind` is `value`: each `def` that isn't a function, at the `def`, or at the value of one at the top level; each statement whose value isn't `()`, a block's last among them and a script's top-level ones; and the body of a lambda that isn't a block. A value of a type `dbg` couldn't write is written by its type, `<value of MArray(int)>`, and a sequence always is, `<sequence of int>`, so that writing it never runs it. Only the code of the script, or of the package's own files, is shown, not the library's. It is how the editor's [live mode](./vs-code-extension#live-mode) shows every value, and without `--dbg-to` it is an error. |
| `--timings` | Writes how long each module took to check (see [Timings](#timings)). |

The report of `--memory` says how many blocks of memory the program allocated and freed, how many were still in use
when it stopped, how many objects it built in the memory of one it had finished with rather than allocating (see
[Reference counting](../deep-dives/reference-counting)), how many bytes it allocated in all, the most it had in use
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
error, including one a debug build would defer, and it shows every diagnostic, info and hints included. A test file,
which the manifest's `tests` selects, is checked as `nanyx test` compiles it, and so is every file of a
[test package](../tour/packages#test-packages).

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
browsers, Wasmtime and other WebAssembly runtimes load. A package with no `@entry` is only checked, unless its
modules mark defs [`@component`](../tour/attributes#component) and its host has a component root: then it is a library
component, which exports them and has no `run`.

A program whose host has a component root, as the console host does, is written as a WASI 0.3 component, which
`wasmtime run` or any runtime that implements WASI 0.3 runs ([Hosts](../advanced/hosts#built-as-a-component)).
`--core` writes a core module instead, which only `nanyx` runs. A program with functions marked
[`@component`](../tour/attributes#component) exports them from its component, one with defs marked
[`@import`](../tour/attributes#import) imports those, and its WIT, which another language's bindings are generated
from, is written to `out/wit` beside it
([Components](../deep-dives/components#a-programs-own-exports)).

When the manifest asks for something `nanyx.lock` doesn't lock (a new dependency, or a requirement that no longer
accepts the version locked), `nanyx build` and `nanyx run` choose versions again and download them first, as
[`nanyx pull`](#nanyx-pull) does, saying on stderr what they lock.

| Option | What it does |
|--------|--------------|
| `--out <path>`, `-o <path>` | Writes the program to another file, making its directory if need be. |
| `--release` | Refuses any error, and leaves out every [`dbg`](../stdlib/console#dbg): `dbg(x)` compiles to `x`. Without it, a build is a debug build: type and name errors are deferred, and the program is written all the same. |
| `--core` | Writes a core module, rather than the component that a host with a component root otherwise has written. |
| `--wat` | Writes WebAssembly text, to `out/<package name>.wat`, rather than the binary format: the same program, for reading. For a component, it is the core module the component wraps. |
| `--locked` | Leaves `nanyx.lock` as it is: a manifest that asks for something it doesn't lock is an error, naming what. Use it in CI, so that a manifest change pushed without its lockfile fails rather than resolving something new. |
| `--timings`, `--host <host>` | As for `run`. |

## `nanyx wasm`

`nanyx wasm file.nyx` compiles a script to a WebAssembly module, written beside it as `file.wasm`, or to `--out <path>`
(`-o`). With `--wat` it writes WebAssembly text, `file.wat`, instead. Like `build`, it is a debug build unless you pass
`--release`.

## `nanyx test`

`nanyx test` runs the tests of a package, the defs marked [`@test`](../tour/attributes#test) beside its code and in the
files its manifest's [`tests`](../tour/packages#test-files) selects, or of a script, whose top-level statements it
doesn't run. Given a test file, one the manifest's `tests` selects, it runs that file's tests, in its package's test
build. It builds them once, under the test host, and runs each test in a fresh instance of the build, so a test
that crashes fails alone, and reports each as it ends:

```text
Running 3 tests in geometry

PASS geometry.`adds points`
FAIL geometry.`scales points`  src/geometry.nyx:18:1
  src/geometry.nyx:20:3: assert(scale(p, 2) == Point(x = 2, y = 4))
    scale(p, 2) = (x = 2, y = 5)
    p = (x = 1, y = 2)
    geometry_scales-20-points (src/geometry.nyx:20)
  Output:
    scaling (x = 1, y = 2)
PASS geometry.`reads the sample config` (io)

2 passed, 1 failed
Seed 4812: run them again with the same random numbers with --seed 4812
```

A test passes if it finishes, and fails if it crashes, as an `assert` that doesn't hold does, or runs for longer than
its time limit. A failure shows why, with the stack, and what the test printed, which a test that passes doesn't show.
A test's `Random` is seeded, so a test that draws random numbers draws the same ones when it is run with the same seed.
A test may require only `Async`, `Console` and `Random`, which the test host provides (`NYX0312`), but one marked
[`@test(#io)`](../tour/attributes#test), which runs with the console host's real `Console`, `Env` and `FileSystem`,
in the package's directory or the script's, with the environment's variables; the report marks it `(io)`. One marked
[`@test(#crashes)`](../tour/attributes#test) passes only if it crashes, and the report marks it `(crashes)`; if it
finishes, it fails with `It finished, and was expected to crash`, and if it crashes with a message that doesn't contain
the text `#crashes("text")` gives, it fails saying so, with the crash.

`nanyx test` exits with 0 when every test passes, and 1 when one fails or the tests can't be built. It is a debug build,
as `run` is: a type or name error in any module is reported, and fails only the tests that reach it, with its message,
while the others pass or fail on their own ([Deferred errors](../tour/error-handling#deferred-errors)). An error that
can't be deferred, such as a syntax error, stops the build, and no test runs.

| Option | What it does |
|--------|--------------|
| `--filter <text>` | Runs only the tests whose full name, as `` geometry.`adds points` ``, contains the text. |
| `--test <name>` | Runs only the test of that full name, exactly, as the report writes it or without its backticks. Several names go one on each line, a form mainly for tools such as an editor: in a shell, ``--test $'geometry.adds\nshapes.squares'``. A name that names no test is an error, which exits with 2. |
| `--module <name>` | Runs only the tests of that module. A module with no tests is an error, which exits with 2. |
| `--files-in-memory` | Runs each `@test(#io)` test with its files in memory, over the real ones, as [`nanyx run --files-in-memory`](#nanyx-run) runs a program: it reads the real files, and nothing it writes, appends, makes or deletes reaches the disk. Its `Console` and `Env` are still the real ones. The tests are built from the test host's root for this, `test.host.memory`, and a test build without the flag is built as before. The editor's live mode runs tests so. |
| `--list` | Lists the tests that would run, each with where it is written, and runs none. |
| `--seed <number>` | Seeds each test's `Random` with the number. Without it, the seed is chosen at random, and printed when a test fails. |
| `--timeout <seconds>` | How long a test may run before it is stopped, and fails: 10 seconds by default. |
| `--release` | Refuses any error, and compiles the tests as a release build does. |
| `--timings` | As for `run`. |
| `--format <text \| json>` | How to write the report: `text`, for a person, the default, or `json`, for a tool such as an editor ([JSON](#json)). |

### JSON

With `--format json`, `nanyx test` writes one JSON object on each line of standard output, each with a `type`:

- **`diagnostic`**, first, for each error and warning the build reports: its `code`, `severity` (`error`, `warning`,
  `info` or `hint`), `headline`, `message`, and, when it has a place, its `file`, from the package's root, `line` and
  `column`, each from 1; and `deferred`, whether the tests run in spite of it.
- **`start`**: the package's or script's `name`, how many `tests` run, the `seed`, and how many `deferredErrors` there
  are.
- **`test`**, for each test as it ends: its full `name`, as the text report shows it, its `module` and own name
  (`test`), the `file`, `line` and `column` it is written at, whether it is `io`, the crash it expects (`crashes`:
  `null`, `{}` for `@test(#crashes)`, or `{"text": ...}`), its `outcome` (`passed`, `failed`, `timedOut` with
  `timeoutSeconds`, `didNotCrash`, or `crashedOtherwise` with the text it `expected`), what it printed (`output`),
  what it wrote to its standard error (`errors`), and each [`dbg`](../stdlib/console#dbg) it reached, in order (`dbg`),
  with its `file`, `line`, `column` and `value`, which `output` has too. A test that failed or crashed otherwise has a `failure`: its `kind` (`crashed`, `trapped`, `arithmetic`, `stackOverflow`, `outOfMemory`,
  `exited` or `other`), its `message`, the `place` of the program's code a crash was in, with its `file`, `line` and
  `column`, and its `stack`, innermost first, each frame's `function` with its `file` and `line`.
- **`summary`**, last: whether the tests were `built`, how many `passed` and `failed`, and the `seed`. A build that
  fails writes its diagnostics and a summary with `built` false, and a package with no tests a summary alone.

With `--list`, it writes the diagnostics, then a `test` object for each test that would run, with the fields that say
which test it is and no outcome, and a `summary`.

A package whose manifest can't be read writes its error to standard error, as text, and nothing to standard output.
The exit code is as with text.

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
│ error NYX0102: Unknown function 'lookup'
│   --> src/main.nyx:12:5
│    |
│ 12 |     lookup(id)
│    |     ^^^^^^
│    = 'lookup' is not in scope
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
manifest, `acme.demo.nyxp`, with the official linter on and the console host, both at the compiler's version, the
compiler's minor version as the oldest the package builds with, `nanyx = "0.54"`, and `src/main.nyx`, whose `@entry` prints "Hello, World!". A package's name is lower case, with words separated by dots.

## `nanyx migrate`

`nanyx migrate` turns a script, or every `.nyx` file in a directory (the current one by default), into a package:

- It writes a manifest, `<name>.nyxp`, beside the files, named by `--name` or by the directory, at version `0.1.0`,
  whose `sources` select the files it migrated.
- Each `::` [directive](../getting-started/files-and-projects#directives) becomes the manifest entry it stands for,
  and is removed from its file, with the `#!` line: `::install acme.util ../util` becomes
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
nanyx install acme.json                  # from the package index, at its newest version
nanyx install acme.json --version 1.2    # from the package index, at 1.2 or a later version compatible with it
nanyx install acme.util --path ../util   # the package in that directory
nanyx install nanyx.cli         # a package the compiler ships, at the compiler's version
```

A package from the index is written at the versions `--version` accepts, or else at the newest version the index has,
`version = "1.4.2"`, which accepts it and the later versions compatible with it. It is then locked in `nanyx.lock` and
downloaded, as `nanyx pull` does, and `install` says which version it locked. If no versions satisfy every dependency
with it, `install` explains why (`NYX0047`) and leaves the manifest as it was. A directory must hold a package of the
name you give, and a package already listed is an error.

## `nanyx pull`

`nanyx pull` makes a package's dependencies available to build. When the manifest asks for something `nanyx.lock`
doesn't lock (a new dependency, or a requirement the version locked no longer satisfies), it chooses versions from the
package index and writes the lockfile, saying what it locks: the newest version each package's dependencies accept,
keeping every version the lockfile already locks while it is still accepted, so nothing is upgraded as a side effect.
An unlisted version isn't chosen anew, but stays where it is locked already, with a warning (`NYX0051`), and a version
that needs a newer compiler isn't chosen. When no versions satisfy every dependency, it explains why, a step at a time (`NYX0047`).

Then it downloads into the package cache each package the lockfile locks that isn't there yet, checking each against
the SHA-256 the lockfile records, checks that each dependency of a package is there, as a build would, and lists them:
`acme.util 0.3.0 (../util)`, `acme.web 1.2.0 (downloaded)`.

Given a script, `nanyx pull script.nyx` downloads the exact version each of its `::install` lines pins, and the
packages those depend on, the newest each accepts, checking each against the SHA-256 the index records, and lists them:
`acme.web 1.2.0`. A script has no lockfile: the versions it writes are its pins. `nanyx run` does the same for a script
before it runs it.

It fetches each dependency on GitHub, a package or a file, at the commit it is pinned to, into the cache too. Set
`GITHUB_TOKEN` to a token of yours to have GitHub count its requests against your account.

The cache is `.nanyx/packages` in your home directory, or the directory `NANYX_PACKAGES` names. Packages are
downloaded from the [package index](https://packages.nanyx.dev/), or from the one `NANYX_INDEX` names: an
address, or a directory on disk.

## `nanyx pack`

`nanyx pack` packs a package as a `.nyxpkg`, the file a release of it carries, and prints its SHA-256:

```bash
nanyx pack                    # writes acme.util-0.3.0.nyxpkg beside the manifest
nanyx pack --out dist ../util
```

A `.nyxpkg` is a zip of the package's manifest, its source files and its `api.json`, which records what each module it
exports with `export module` exports, with its type, and each nominal type's parameters' variances, which its
definition decides however it is written, so that the next version can be compared with it. The same files
pack to the same bytes on every machine: each file is written as UTF-8 with LF line endings and no byte-order mark,
whatever the checkout gives it, so a Windows checkout with CRLF line endings packs as a Linux one does, and a file that
isn't UTF-8 is an error (`NYX0049`). A package that doesn't compile can't be packed. A package with a dependency found by its path, or on GitHub, can't be packed, since that dependency isn't there
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

## `nanyx api-check`

`nanyx api-check` compares two versions of a package, each a `.nyxpkg`, as the package index does before it records a
new release: the newer one's version must be a big enough bump from the older one's for the changes between the APIs
their `api.json` files record, by the rules `nanyx bump` follows.

```bash
nanyx api-check acme.util-1.0.0.nyxpkg acme.util-1.1.0.nyxpkg
```

It lists the changes as `nanyx bump` does, then says the bump is big enough. When it isn't, it lists on stderr the
changes that need a bigger one and exits with 1; so it does for a `.nyxpkg` with no `api.json`, and for two different
packages.

## `nanyx publish`

`nanyx publish` publishes the version a package's manifest gives, saying what it is doing at each step and stopping
at the first that fails:

1. it checks that the manifest says the oldest compiler the package builds with, `nanyx`, which the package index
   records, and that the package builds and that its files are formatted;
2. it compares its API with the newest version published below it, as [`nanyx api-check`](#nanyx-api-check) does, and
   refuses a version published already or a bump too small for the changes, listing them;
3. it packs the package, as [`nanyx pack`](#nanyx-pack) does;
4. it checks that you can create releases on the GitHub repository the manifest's `repository` names and, for a
   package the index hasn't registered yet, that you can open a pull request on
   [nanyx-lang/packages](https://github.com/nanyx-lang/packages), so that nothing is made if either would fail;
5. it creates the release `v<version>` on the package's repository, with the `.nyxpkg` as its asset;
6. for a package the index hasn't registered yet, it opens the pull request that registers it.

A pull request to the index comes from a branch of nanyx-lang/packages if you can push to it, and otherwise from your
fork of it, which `publish` makes if you have none. A branch is named `register-<package>`, with `-2`, `-3` and so on
after it when that name is taken.

If a publish stops after it has created the release, for example because the pull request couldn't be opened, run it
again: a release that holds the same `.nyxpkg` (GitHub records its SHA-256), or none yet, is the one it made, so it
goes on to the pull request. A release with a different `.nyxpkg` is refused, since a published version can't change.

The index's sync records the release within the hour (for a new package, once a maintainer has merged its
registration). `publish` signs in to GitHub with `GITHUB_TOKEN`, if it is set, or else with the GitHub CLI's login
(`gh auth login`); the token must be able to create releases on the repository, and to push to or fork
nanyx-lang/packages and open pull requests on it. Once a package is registered, a version can be published from its
repository's GitHub Actions workflow with the workflow's own `GITHUB_TOKEN`, given `permissions: contents: write`.

## `nanyx unlist`

`nanyx unlist acme.web 1.2.0` opens the pull request on the package index that marks a version unlisted. Once it is
merged, no new resolution chooses the version, but a lockfile that locks it, or a script that pins it, keeps building.
A command that reads the index for such a version, `pull`, `install`, `build` or `run`, warns that it is unlisted and
goes on ([`NYX0051`](./errors#nyx0051)); one that reads it only from the package cache doesn't.
`unlist` signs in as `publish` does, opens its pull request from a branch `unlist-<package>-<version>` in the same place
`publish` would, and refuses a version the index doesn't have or has unlisted already.

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
- `--yield-at-every-wait`, on `run`, makes every call that can wait stop once and be resumed, which runs each way a
  function that waits has of stopping and carrying on (see [Async](../deep-dives/async#how-tasks-are-compiled)). A
  program that behaves differently with it points at a bug in the compiler.
- `nanyx stdlib-docs <directory>` writes the reference on each page of the docs site's
  [Standard Library](../stdlib/standard-library) section from the library's doc comments, or, on a page that pins a
  published package in its front matter (`package: nanyx.json 0.1.0`), from that version's, and with `--check` lists
  the pages that are out of date and exits with 1.
