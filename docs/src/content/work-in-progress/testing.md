---
title: "Testing"
description: "Writing tests with @test and assert, and running them with nanyx test"
order: 7
---

This page proposes how Nanyx code is tested: a test is a definition marked `@test`, it checks what it expects with `assert`, and `nanyx test` runs every test in a package or script.

# Motivation

Other parts of the design already say what testing must do:

- [Hosts](./hosts#the-test-runner-is-a-host) says `nanyx test` runs the program under a test host. That host finds test functions instead of an `@entry`, and provides fake or recording versions of the contexts the code needs.
- [Deferred errors](https://github.com/nanyx-lang/nanyx/blob/main/compiler/design/deferred-errors.md) says every test runs in a debug build. A test that reaches a type error fails with that error, and the others pass or fail on their own.
- The [principles](../foundations/principles) say a developer is never blocked from running their tests because, say, a commented-out line left a variable unused.

This page fills in the rest: how a test is written, how it fails, and how the runner reports it.

# Writing a test

A test is a definition marked with the `@test` [attribute](../tour/attributes). Its name is the definition's name, and the module it is in groups it with the module's other tests:

```nanyx
module geometry

type Point = (x: int, y: int)

def add: (Point, Point) -> Point = { a, b ->
  Point(x = a.x + b.x, y = a.y + b.y)
}

@test
def addsPoints = {
  def p = add(Point(x = 1, y = 2), Point(x = 3, y = 4))
  assert(p.x == 4)
  assert(p == Point(x = 4, y = 6))
}
```

A test takes no arguments and returns `()`. It passes if it finishes, and fails if it panics: an `assert` that does not hold, a [deferred error](#deferred-errors), a non-exhaustive match or any other panic.

Tests are only compiled into a test build. `nanyx run` and `nanyx build` leave them out, so code that is not a test cannot refer to one, and doing so is an error. Linters do not report a test as unused.

## Tests beside the code

A test can sit in the module it tests, as `addsPoints` does above. There it can see the module's private definitions, so a helper can be tested without exporting it. Rust's `#[cfg(test)]` modules and Zig's `test` blocks take the same approach.

## Tests in their own files

A package can also keep tests in files of their own. The manifest's `tests` key selects them, as `sources` selects the rest:

```toml
[package]
name = "geometry"
version = "0.1.0"
sources = ["src/**/*.nyx"]
tests = ["tests/**/*.nyx"]
```

Each test file declares a named module, as every file in a package does. It is part of the package in a test build and is left out of every other build. It can import the package's modules and use what they export, but not their private definitions. If `tests` is left out, a package has no test files, and its tests are the ones beside its code.

## Scripts

`nanyx test file.nyx` runs the tests in a script. The script's top-level statements are its entry, so they are not run, as a package's `@entry` is not.

# Asserting

`assert` takes a `bool`. If it is `false`, `assert` panics with the expression it was given and the values in it:

```nanyx
@test
def scalesPoints = {
  def p = Point(x = 1, y = 2)
  assert(scale(p, 2) == Point(x = 2, y = 4))
}
```

```text
FAIL geometry.scalesPoints  src/geometry.nyx:18:3
  assert(scale(p, 2) == Point(x = 2, y = 4))
    scale(p, 2) = Point(x = 2, y = 5)
    p = Point(x = 1, y = 2)
```

`assert` is built in, like `dbg`, because only the compiler has what that message needs: the source text of the expression, and a printer for the type of each value in it. For a comparison it prints each side that is not a literal, then each name the expression reads. That is why there is no `assertEqual` or `assertContains`: `assert(xs \contains(3))` already prints `xs`. It prints values the way [`dbg`](./printing-values#dbg-prints-any-value) does, ignoring any `ToString` the type has.

`assert` is not only for tests. It works anywhere, and outside a test it is a panic with a good message, as in Rust and Zig. Panicking is not an effect in Nanyx, so `assert` needs no context: `assert: bool -> ()`.

A test stops at the first `assert` that fails. Each test runs on its own (see [Running tests](#running-tests)), so a failure never affects the tests after it.

# Contexts

A test runs under the test host, which provides `Console` and nothing else. What a test prints is captured, and shown only if the test fails.

The code under test often needs more, such as `FileSystem` or `Clock`. A test provides those itself, with the `use` that any code has for [loading a context](../advanced/contexts#loading-a-context):

```nanyx
@test
def loadsDefaultsWhenMissing = {
  use FileSystem(read = { _ -> #error(#notFound) })
  assert(config.load("app.toml") == Config.default)
}
```

A test that needs a context it does not load is a compile-time error, as an entry is that needs a context its host doesn't provide:

```text
error: the test `loadsDefaults` requires context `FileSystem`, which the test host does not provide
  load a fake with `use FileSystem(...)`, or mark the test `@test(#io)` to use the real one
```

So a unit test never touches the file system, the clock or the environment unless it says so, and the compiler checks that. This is the check [Hosts](./hosts#the-test-runner-is-a-host) wants: the same program runs under a test host with no IO.

## Integration tests

A test marked `@test(#io)` runs with the console host's contexts (`Console & FileSystem & Env & Clock`), real ones, for tests that are meant to read files or the clock:

```nanyx
@test(#io)
def readsTheSampleConfig = {
  assert(config.load("tests/sample.toml") == #some(Config(port = 8080)))
}
```

Its output is still captured.

# Running tests

```text
nanyx test [path] [--filter text] [--release]
```

`nanyx test` takes a package's directory or manifest, or a script, as `nanyx run` does. It runs every test whose full name (`geometry.addsPoints`) contains the filter, and reports each one:

```text
Running 3 tests in geometry, with 1 deferred error

PASS geometry.addsPoints
FAIL geometry.scalesPoints  src/geometry.nyx:18:3
  assert(scale(p, 2) == Point(x = 2, y = 4))
    scale(p, 2) = Point(x = 2, y = 5)
    p = Point(x = 1, y = 2)
FAIL geometry.parsesPoint  src/geometry.nyx:24:14
  error E001: Type mismatch (deferred from compile time)
    Expected type `int` but found type `string` in the expression `x + 1`

1 passed, 2 failed
```

It exits with 0 if every test passes and 1 if any fails, or if the tests could not be built.

## One instance per test

A panic ends the WebAssembly instance it happens in, so tests cannot share one: the first failure would stop the rest. Instead, the runner compiles the package once, to a module that exports each test as a function, and creates a fresh instance of that module for each test. Creating an instance from a compiled module is cheap, and it gives each test its own memory, so no state leaks from one test to the next. Tests can later run in parallel, an instance each.

A test that runs for longer than a time limit fails, with a message that says so; `--timeout` sets the limit.

## Deferred errors

`nanyx test` is a debug build, as `nanyx run` is. A type or name error is reported, and the test that reaches it fails with that error's message, as `parsesPoint` does above. The others run as usual, so a change can be tested while other code is still broken. `nanyx test --release` refuses to run if there is any error.

## Editors

`nanyx test --format json` writes one JSON object per test: its name, file, span, result and captured output. The VS Code extension uses it to fill the editor's test explorer and to put a "Run test" link above each `@test`.

# Left for later

These fit the design but are not part of the first version:

- **Parametrised tests.** A loop inside a test covers this for now, and `assert` prints the loop variable when the expression reads it.
- **Property-based testing.** A library, with a context that generates values, rather than a language feature.
- **Snapshot tests**, which compare a value's printed form with one stored in a file.
- **Expecting a panic.** A test cannot catch a panic, but the runner sees one, so an attribute such as `@test(#panics)` could pass a test only if it panics.
- **Docs as tests.** Running the code blocks in these docs as tests, which [compilation](https://github.com/nanyx-lang/nanyx/blob/main/compiler/design/compilation.md) already wants.

# Prior art

- **Rust**: `#[test]` functions beside the code in `#[cfg(test)]` modules, `cargo test`, and `assert!` that panics anywhere.
- **Zig**: `test` blocks beside the code, compiled only by `zig test`.
- **Go**: `go test`, with tests in `_test.go` files in the same package.
- **pytest** and **Elixir's ExUnit**: a plain `assert` whose failure message shows the values in the expression, so no family of `assertEqual` functions is needed. Groovy and Kotlin call this "power assert".
- **Roc**: `expect`, built into the language, and `roc test`.

# Open questions

- **`assert` in release builds.** Should a release build keep every `assert`, as Rust's `assert!` does, or should there also be a debug-only form, like Rust's `debug_assert!`?
- **A message.** Should `assert` take an optional message, `assert(ok, "the cache was warm")`, or are the printed values always enough?
- **Names that are sentences.** A test's name is a definition's name, so `addsPoints`, not "adds two points". Is that enough, or does `@test` want a description, such as `@test("adds two points")`?
- **Private definitions in test files.** Should a file selected by `tests` be able to reach the private definitions of a module it names, as Go's tests in the same package can?
