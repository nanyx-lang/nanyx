---
title: "Test builds"
description: "How the compiler finds a package's tests, checks them, and builds a module that runs one at an index"
order: 23
---

A test is a def marked `@test` ([Attributes](../tour/attributes#test)). Only a test build has tests: it compiles a
package's sources and its test files to a core module whose entry point, under the test host, `nanyx.test.host`, runs the
test at the index the host gives it. The design of the runner, `nanyx test`, is on
[Testing](../work-in-progress/testing).

# Finding the tests

`Attributes.applyAttributes` records each def marked `@test` in `ComponentDefs.Tests`, with its span, and keeps the
def as it is. `Library.fs` adds the module's own to `TypedModule.Tests`, a `Test` each: its module, its name, the name
of its def in the merged module, and its span. Each import carries the imported module's tests into the importer's
`Tests`, renamed as the import renames the def (`ModuleLinking.testsForImport`), as `ComponentExports` are carried, so
the root of a build has every test of the modules it reaches.

A manifest's `tests` selects a package's test files (`Package.testFiles`). `Package.withTests` adds them to its
sources and gives the package the test host, found as `--host` finds one (`Package.withHost`), as its `TestHost`, and
`Package.testBuild` checks every file. The program's modules keep the package's own host, whose prelude they see, so
a module that calls `args()` under the console host, or names `Request` under the HTTP host, compiles in a test build
as it does in any other (#2996). Only the build's root, and the test host's own package, run under the test host
(`Library.hostOf`). A library has no host, nor a prelude, so its modules run under the test host, and see its prelude.

# Checking them

`TestChecks.errors` checks a module's tests when it is compiled, in every build:

- a test's type is `()` or `() -> ()`, which a block is (`NYX0310`);
- code that isn't a test, the module's top-level expressions and its other defs, never refers to one (`NYX0311`): only
  a test build has them;
- a test requires only the contexts the test host provides, `Hosts.testContexts`: `Console` and `Random` (`NYX0312`).

The official linter's unused-definitions rule skips a test, which nothing but a test build calls.

# Running one

A test beside its code is private to its module, so no other module can name it. Instead, in a test build
(`PackageModules.TestRoot`), `applyAttributes` gives each of the program's modules that has tests one more def, exported,
`Attributes.testDispatcher`:

```nanyx fragment
export def __tests = { index ->
  if index == 0 then `adds points`() else if index == 1 then `scales points`() else ()
}
```

Its type, and the contexts it requires, which are its tests', are inferred, as a def's written without a type are. Its
name and its parameter are declared, as the parser declares a written def's (`Attributes.withDispatcherDeclared`):
inference finds a def's symbol, and an unannotated one's contexts, by where it is declared, and a def that isn't
declared loses the contexts it requires. A program file's `@entry` is an ordinary def in a test build: the build runs
the tests, not the program.

`Package.testResult` compiles the build's root, `__tests.nyx` in the package's directory, which is none of its files
(`Package.testRootIn`). It imports each module with tests, and its `@entry` takes an index and counts through each
module's tests in turn, in the order of the package's files (`Package.testRootSource`):

```nanyx fragment
import geometry as __test0
import geometry.tests as __test1

@entry
def run = { index -> if index < 1 then __test0.__tests(index - 0) else if index < 3 then __test1.__tests(index - 1) else () }
```

The root's `Typed.Tests` lists the tests in that order. The test host's `run` asks the runtime's `nanyx:test` which
index to run, and calls the entry point with it, under a `Console` whose output goes to the runtime and a `Random` seeded
with the runtime's seed:

- `index() -> i32`, the index of the test to run;
- `seed() -> i64`, the seed of its `Random`, as `random.seeded` takes one;
- `write(text: i32, length: i32)` and `writeError(text: i32, length: i32)`, what the test prints.

The program runs in the module's start function, so each test runs in an instance of its own, and a test that crashes
ends only that instance.

A test build is a debug build, as `nanyx run`'s is, so a type or name error in any module, the root's imports
included, is deferred (see [Deferred errors](./compilation#deferred-errors)): `nanyx test` reports each once, says
`Running 3 tests in tested with 1 deferred error`, and runs every test. A test that reaches the error fails with its
message and stack, as with any crash, and the others pass or fail on their own
([#2967](https://github.com/nanyx-lang/nanyx/issues/2967)). An error that can't be deferred, such as a syntax error,
stops the build, and no test runs. `nanyx test --release` refuses to run if there is any error.

# `#io` tests

`@test(#io)` marks a test that runs with the console host's real `Console`, `Env` and `FileSystem`, and `Async`
(`Hosts.ioTestContexts`). `Attributes.applyAttributes` records whether each test is marked so (`MarkedTest.Io`), which
`TypedModule.Tests` carries (`Test.Io`); any other argument of `@test` is `NYX0013`. `TestChecks` checks a `#io` test
against those contexts, and any other against `Hosts.testContexts`. When every context a test lacks is one `#io`
provides, `NYX0312` says so, with a fix that adds `(#io)` after the test's `@test`.

The `nanyx.tests` table gives each test a byte, 1 for a `#io` test, and `nanyx:test`'s `io()` gives it for the test the
instance runs. The test host loads the real contexts for it (see [Hosts](./hosts)), and the runner runs it in the
package's directory, or the script's, which it may read and write, with the environment variables of the command's
shell; any other test reaches no directory and sees no variables. What a `#io` test's real `Console` writes to standard
output is kept with what it printed (`TestRun.Output`).

# The module

`TestBuild.assemble` compiles the root, then `WasmBinary.withTests` adds a custom section, `nanyx.tests`, that lists
each test at its index: its module, its name, and its file, line and column, each from 1, the file named as a crash's
place names it. The runtime reads it with `TestTable.read`, to choose tests by name and to say where each is.

# The runner

`WasmTester.run`, in the runtime, runs the tests of a test build. It compiles the module once, with Wasmtime's epoch
interruption, then runs each test it is asked for, one at a time, in a store and an instance of its own, so a test that
crashes fails alone and no state leaks from one test to the next. It gives each test's `TestRun`:

- its outcome: `Passed`, `Failed` with the `RunError` the instance stopped with, or `TimedOut` once it has run for
  longer than its time limit, 10 seconds unless it is given another (`WasmTester.defaultTimeout`), after which a timer
  moves the engine's epoch on and the store is interrupted;
- what it printed through `nanyx:test` and each `dbg` it reached, in the order it wrote them: a test build is compiled
  with `DbgToEmbedder`, so a `dbg` goes to the runtime rather than to standard error;
- what it wrote to its standard error itself.

A test build is compiled with `CrashToRuntime`, so a crash, an `assert` that doesn't hold among them, comes with its
message, as well as its place and stack (see [Crash stacks](./crash-stacks)).
