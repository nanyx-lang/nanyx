---
title: "Proposal: Testing"
description: "Writing tests with @test and assert, and running them with nanyx test"
order: 7
---

This page proposes how Nanyx code is tested: a test is a definition marked `@test`, it checks what it expects with `assert`, and `nanyx test` runs every test in a package or script.

# Motivation

Other parts of the design already say what testing must do:

- [Hosts](./hosts#the-test-runner-is-a-host) says `nanyx test` runs the program under a test host. That host finds test functions instead of an `@entry`, and provides fake or recording versions of the contexts the code needs.
- [Deferred errors](../tour/error-handling#deferred-errors) mean every test runs in a debug build. A test that reaches a type error fails with that error, and the others pass or fail on their own.
- The [principles](../meta/principles) say a developer is never blocked from running their tests because, say, a commented-out line left a variable unused.

This page fills in the rest: how a test is written, how it fails, and how the runner reports it.

# Decided

Settled with the maintainer on 26 November 2026 ([#2936](https://github.com/nanyx-lang/nanyx/issues/2936)):

- **Crash boundaries aren't a prerequisite.** The runtime runs each test in a fresh instance, as it answers each
  request in one for the HTTP host ([Test builds](../deep-dives/test-builds#the-runner)). The language-level `isolate` on
  [Concurrency and crash boundaries](./concurrency-and-crash-boundaries#isolate-a-crash-boundary-as-a-value) waits for
  a use the runner can't cover.
- **The test host is a shipped package,** `nanyx.test.host`, beside the console and HTTP hosts. Its root imports the
  runtime's `nanyx:test`, as the HTTP host's imports `nanyx:serve`, to learn which test to run.
- **What it needs first is crash reports as data:** a crash's message, place and stack, which today reach the runtime
  only partly, the message as text on stderr.
- **The test host provides `Async`, `Console` and `Random` only,** as [Contexts](#contexts) says. Its `Async` waits on a
  real clock; virtual time, below, would replace it.
- **`assert` checks in release builds too, and takes no message.**

# Writing a test

A test is a definition marked with the `@test` [attribute](../tour/attributes). Its name is the definition's name, usually a sentence in backticks, and the module it is in groups it with the module's other tests:

```nanyx fragment
module geometry

type Point = (x: int, y: int)

def add: Point, Point -> Point = { a, b ->
  Point(x = a.x + b.x, y = a.y + b.y)
}

@test
def `adds points` = {
  def p = add(Point(x = 1, y = 2), Point(x = 3, y = 4))
  assert(p.x == 4)
  assert(p == Point(x = 4, y = 6))
}
```

A [name in backticks](../tour/naming-conventions#names-in-backticks) can be any text but a backtick, a line break or a dot, so a test reads as what it checks, and the runner reports it as `` geometry.`adds points` ``. A plain name, `def addsPoints`, works too.

A test takes no arguments and returns `()`. It passes if it finishes, and fails if it crashes: an `assert` that does not hold, a [deferred error](../tour/error-handling#deferred-errors), a non-exhaustive match or any other crash.

Tests are only compiled into a test build. `nanyx run` and `nanyx build` leave them out, so code that is not a test cannot refer to one, and doing so is an error (`NYX0311`). Linters do not report a test as unused. This is built: see [Test builds](../deep-dives/test-builds).

## Tests beside the code

A test can sit in the module it tests, as `` `adds points` `` does above. There it can see the module's private definitions, so a helper can be tested without exporting it. Rust's `#[cfg(test)]` modules and Zig's `test` blocks take the same approach.

## Tests in their own files

A package can also keep tests in files of their own. The `tests` field of the manifest's `package` selects them, as `sources` selects the rest:

```nanyx fragment
(
  package = (name = "geometry", version = "0.1.0", sources = ["src/**/*.nyx"], tests = ["tests/**/*.nyx"])
)
```

Each test file declares a named module, as every file in a package does. It is part of the package in a test build and is left out of every other build. It can import the package's modules and use what they export, but not their private definitions. If `tests` is left out, a package has no test files, and its tests are the ones beside its code.

## Test packages

Built ([#3095](https://github.com/nanyx-lang/nanyx/issues/3095)): a package whose manifest gives `tests` and no `sources`
holds only the tests of the packages it depends on, in a sibling folder, as a .NET test project does
([Test packages](../tour/packages#test-packages)). It has no source files, needs no host and exports nothing; only
`nanyx test` builds it. It can depend on an application, whose `@entry` is an ordinary def in the test build and whose
modules keep its host ([Test builds](../deep-dives/test-builds#test-packages)). Its tests import what the package exports,
so a private helper is still tested beside its code.

An editor checks a file under the manifest's `sources` as one of its package's; a file that only `tests` selects, as all
of a test package's are, is still checked on its own, as a script ([#3098](https://github.com/nanyx-lang/nanyx/issues/3098)).

## Scripts

`nanyx test file.nyx` runs the tests in a script. The script's top-level statements are its entry, so they are not run, as a package's `@entry` is not.

# Asserting

`assert` is built: see [`assert`](../stdlib/console#assert). It takes a `bool`, and if it is `false` it crashes with
where it is, the `assert` as written and the values in it. In a test, the runner reports that under the test's name:

```nanyx fragment
@test
def `scales points` = {
  def p = Point(x = 1, y = 2)
  assert(scale(p, 2) == Point(x = 2, y = 4))
}
```

```text
FAIL geometry.`scales points`  src/geometry.nyx:16:1
  src/geometry.nyx:18:3: assert(scale(p, 2) == Point(x = 2, y = 4))
    scale(p, 2) = (x = 2, y = 5)
    p = (x = 1, y = 2)
```

The place after the test's name is where the test is written. Under it comes why it failed: for a crash, the file, line
and column of the program's code that crashed, from the crash's report ([Crash
stacks](../deep-dives/crash-stacks#the-crash-report)), then what it wrote, `crash: boom` for a `crash("boom")`. An
`assert`'s message starts with its place already, so it is written once.

`assert` is built in, like `dbg`, because only the compiler has what that message needs: the source text of the expression, and a printer for the type of each value in it. For a comparison it prints each side that is not written out in full, then each name the expression reads, but one bound inside it. That is why there is no `assertEqual` or `assertContains`: `assert(xs \contains(3))` already prints `xs`. It prints values the way [`dbg`](../stdlib/console#dbg) does, ignoring any `ToString` the type has.

`assert` is not only for tests. It works anywhere, and outside a test it is a crash with a good message, as in Rust and Zig. Crashing is not an effect in Nanyx, so `assert` needs no context: `assert: bool -> ()`. A release build keeps every `assert`, and it takes no message: the values it prints are the message.

A test stops at the first `assert` that fails. Each test runs on its own (see [Running tests](#running-tests)), so a failure never affects the tests after it.

# Contexts

Built: a test runs under the test host with `Console`, a seeded `Random` and `Async`, a test that requires another context is
`NYX0312`, and `@test(#io)` runs one with the console host's real `Console`, `Env` and `FileSystem`
([`@test`](../tour/attributes#test), [Test builds](../deep-dives/test-builds#io-tests)). What is left:

- **A generated fake.** `NYX0312` could offer a fix that loads a fake of the missing context with `use`, each of its
  members a function that crashes until the test fills it in ([#2932](https://github.com/nanyx-lang/nanyx/issues/2932)).
- **`@test(#io)` under another host.** It gives the console host's contexts, which a program under the HTTP host, whose
  prelude has no `Env` or `FileSystem`, can't name, so such a test is `NYX0312` for now.

# Running tests

`nanyx test` is built: the [command line](../standard-tools/command-line#nanyx-test) says what it does, and
[Test builds](../deep-dives/test-builds) how. It takes a package's directory or manifest, or a script; it runs each test
in a fresh instance of the test build, whose host runs the test at the index the runtime gives it; it reports each test,
with why one failed and what it printed; and it stops a test that runs past `--timeout`. What is left of the design is
below.

The tests run in a core module, as `nanyx run`'s program does, so a test that reaches a call of an `@import` fails with
`NYX0278` ([Components](../deep-dives/components#a-programs-own-imports)).

## Editors

`nanyx test --format json` writes one JSON object on each line ([#3079](https://github.com/nanyx-lang/nanyx/issues/3079),
[JSON](../standard-tools/command-line#json)), and `--list`, `--test` and `--module` list the tests and run exactly the
ones named ([#3099](https://github.com/nanyx-lang/nanyx/issues/3099)). Built on them
([#3100](https://github.com/nanyx-lang/nanyx/issues/3100)): the VS Code extension fills the editor's test explorer from
`nanyx test --list --format json`, runs a test, a module or a package, shows each result at the place it failed, and
puts a **Run test** link above each `@test` ([VS Code extension](../standard-tools/vs-code-extension#testing)).

## Expecting a crash

Built ([#3078](https://github.com/nanyx-lang/nanyx/issues/3078)): `@test(#crashes)` passes a test only if it crashes,
and `@test(#crashes("text"))` only if the crash's message contains the text; with `#io`, `@test(#io, #crashes)` (see
[`@test`](../tour/attributes#test)). A crash is what `crash`, `orCrash` and a failed `assert` do. A deferred compile
error never counts: a match that doesn't handle every case is one, `NYX0203`, which the compiler defers to run time as it
does a type error, so it fails the test, as a runtime error and a timeout do
([Test builds](../deep-dives/test-builds#tests-that-expect-a-crash)).

# Left for later

These fit the design but are not part of the first version:

- **Parametrised tests.** A loop inside a test covers this for now, and `assert` prints the loop variable when the expression reads it.
- **Property-based testing.** A library, with a context that generates values, rather than a language feature.
- **Snapshot tests**, which compare a value's printed form with one stored in a file.
- **Virtual time.** The test host providing an `Async`, in place of the real one every test has now, and a `Clock` whose `sleep` advances at once, with tasks run in a
  fixed order, so tests of `sleep`, `race` and timeouts run instantly and the same way every time.
- **A file system in memory.** A ready-made `FileSystem`, which a test loads with `use`, that holds its files in memory, so a test can write files and read them back without faking each member by hand.
- **Docs as tests.** Running the code blocks in docs as tests. The compiler's own tests already run the docs' ([compilation](../deep-dives/compilation#docs-examples)); `nanyx test` could do the same for a package's.

# Prior art

- **Rust**: `#[test]` functions beside the code in `#[cfg(test)]` modules, `cargo test`, and `assert!` that panics anywhere.
- **Zig**: `test` blocks beside the code, compiled only by `zig test`.
- **Go**: `go test`, with tests in `_test.go` files in the same package.
- **pytest** and **Elixir's ExUnit**: a plain `assert` whose failure message shows the values in the expression, so no family of `assertEqual` functions is needed. Groovy and Kotlin call this "power assert".
- **Roc**: `expect`, built into the language, and `roc test`.
- **F#** and **Kotlin**: tests named with sentences in backticks, ``` ``adds two points`` ``` and `` `adds two points` ``.

# Plan

| Stage | Issue |
|---|---|
| 1. Crash reports as data: message, span and stack | [#2933](https://github.com/nanyx-lang/nanyx/issues/2933) |
| 2. `assert` | [#2934](https://github.com/nanyx-lang/nanyx/issues/2934) |
| 3. `@test` and test builds: built ([Test builds](../deep-dives/test-builds)) | [#2930](https://github.com/nanyx-lang/nanyx/issues/2930) |
| 4. `nanyx test`, `nanyx.test.host` and `nanyx:test`: built ([Test builds](../deep-dives/test-builds)) | [#2931](https://github.com/nanyx-lang/nanyx/issues/2931) |
| 4a. Defer a type error in an imported module: built ([Test builds](../deep-dives/test-builds#running-one)) | [#2967](https://github.com/nanyx-lang/nanyx/issues/2967) |
| 5. A context the test host lacks, and `@test(#io)`: built ([Contexts](#contexts)) | [#2935](https://github.com/nanyx-lang/nanyx/issues/2935) |
| 6. Virtual time, editors, `@test(#crashes)` and later items | [#2932](https://github.com/nanyx-lang/nanyx/issues/2932) |

# Open questions

- **Dots in test names.** A name in backticks cannot hold a dot, because dots separate the parts of a qualified name, so a test cannot be called `` `parses 1.5` ``. Is that worth lifting for tests?
- **Private definitions in test files.** Should a file selected by `tests` be able to reach the private definitions of a module it names, as Go's tests in the same package can?
