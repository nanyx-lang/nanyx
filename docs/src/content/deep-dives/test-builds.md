---
title: "Test builds"
description: "How the compiler finds a package's tests, checks them, and builds a module that exports each one"
order: 23
---

A test is a def marked `@test` ([Attributes](../tour/attributes#test)). Only a test build has tests: it compiles a
package's sources and its test files, and writes a core module that exports each test, for the runner to call. The
design of the runner, `nanyx test`, is on [Testing](../work-in-progress/testing).

# Finding the tests

`Attributes.applyAttributes` records each def marked `@test` in `ComponentDefs.Tests`, with its span, and keeps the
def as it is. `Library.fs` adds the module's own to `TypedModule.Tests`, a `Test` each: its module, its name, the name
of its def in the merged module, and its span. Each import carries the imported module's tests into the importer's
`Tests`, renamed as the import renames the def (`ModuleLinking.testsForImport`), as `ComponentExports` are carried, so
the root of a build has every test of the modules it reaches.

A manifest's `tests` selects a package's test files (`Package.testFiles`). `Package.withTests` adds them to its
sources, and `Package.testBuild` checks every file. `Package.testResult` then compiles a root that is none of the
package's files, `__tests.nyx` in its directory, that imports each module with a test (`testRootSource`), so its
`Typed.Tests` lists them all, in the order of the package's files.

# Checking them

`TestChecks.errors` checks a module's tests when it is compiled, in every build:

- a test's type is `()` or `() -> ()`, which a block is (`NYX0310`);
- code that isn't a test, the module's top-level expressions and its other defs, never refers to one (`NYX0311`): only
  a test build has them;
- a test requires only the contexts in `TestChecks.hostContexts`, those the test host provides (`NYX0312`).

The official linter's unused-definitions rule skips a test, which nothing calls.

# The module

`TestBuild.assemble` compiles the root with `WasmOptions.Tests`. The code generator keeps each test's function as a
root, though nothing calls it, and exports it as `test#<index>` (`CodeGen.testExport`). `WasmBinary.withTests` then
adds a custom section, `nanyx.tests`, that lists each index's module, name, file, line and column, each from 1, the file
named as a crash's place names it. The runtime reads it with `TestTable.read`.
