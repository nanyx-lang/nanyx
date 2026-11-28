---
title: "Lint builds"
description: "How the compiler reads a linter package, builds its lint build once, and runs it over each module"
order: 23.5
---

A linter written in Nanyx is a package that declares itself a linter, and a project runs it by naming it in its
manifest's `linters` ([Linters](../advanced/linters#writing-a-linter)). The compiler builds it into a lint build, a core
module whose entry point, under the linter host, `nanyx.analysis.host`, lints one module, and runs it over each module
that type-checked. The design, and what is left of moving the official linter onto it, is on
[Linters: compiler design](../work-in-progress/linters). This is the shape a [test build](test-builds) has: a root the
compiler writes, under a host the compiler ships, run once per thing it checks.

# Reading a linter package

`Manifest.check` reads a manifest's `linters`. Each entry is a `LinterEntry`, in `Manifest.LinterPackages`: a package
found by its `path`, or one from the index at a `version`, which `Resolution` locks in `nanyx.lock` as it locks a host,
though it is no part of the program. An entry naming a linter the compiler has built in (`Linters.all`: the official
linter, whose package is the shipped `nanyx.lint`) is read into `Manifest.Linters` too, with its rules' severities. A
script's `::linter` directives are read the same way (`Manifest.linterEntries`), but can name only a linter the compiler
has built in; `Package.scriptLinters` reads its package from the seeded copy, with no lockfile.

The package's own manifest declares it a linter in its `package`'s `linter` field, which `Manifest.check` checks
(`checkLinterDeclaration`) and `LinterPackages.ofManifest` reads into a `LinterDeclaration`: its root module, its name,
which is the first part of its rules' codes, and its rules, each a `Rule` as the built-in ones are.

`Package.ofManifestFrom` reads the linter packages of the package it builds, and only that one's: a dependency's
linters don't run on its dependants. `Package.linterIn` reads each, with the packages it depends on, through the build's
lockfile but apart from the build's own packages. It refuses a package that doesn't declare itself a linter, one that
doesn't depend on `nanyx.analysis`, or depends on another version of it than the compiler's, whose contract the compiler
doesn't answer, and settings that name a rule the linter doesn't declare, each at the `linters` entry or the setting
(`Manifest.linterSettings`). The editor's compile of a manifest reports the same (`Package.compileManifestFrom`).

# Making a linter

The compiler's core (`NanyxCompiler`) doesn't reference the WebAssembly backend or the runtime, so it can't build or run
a lint build itself. It takes linters as functions, `LinterOptions`, as it always has, and `LintBuilds.make` makes one
from a `LinterPackage` given a `LintRunner`: `Assemble`, which turns a lint build's typed root into WebAssembly, and
`Load`, which compiles that once and gives a function that lints a module. The CLI's runner, `LintRunners.Wasm`, gives
`Transpiler.Wasm.LintBuild.assemble` and `Nanyx.Runtime.WasmLinter.load`.

The CLI makes a package's linters as it finds the package (`CliCommands.TryFindPackage`, with `Package.withLinters`), so
`nanyx check`, `build`, `run` and `test` all lint with them; `Package.buildReporting` runs `Package.Linters`. A script's
are made as `nanyx run` and `check` compile it (`Script.compileFileMakingIn`, with `Script.linterOptions`). `nanyx lsp`
gives the language server the same runner (`ServerSettings.LintRunner`), and `ServerSettings.LinterOptionsFor` makes a
document's linters with it (`Package.lintersFor`). A server started without one runs only the built-in linters.

## The official linter

The official linter is moving from F# rules compiled into the compiler (`Linters.official`) into its package,
`nanyx.lint`, whose manifest declares every rule `Linters.official` has, with the same ids, titles, severities and
descriptions; a test checks that they stay the same. `Package.linterOptions` runs the package's lint build as the
official linter (`Linters.withPackage`): the package's reports, and those of the compiler's own rules for each rule the
package doesn't implement yet. `Linters.packageRules` names those it does, and grows as each group of rules moves. Where
the package can't be made, in the compiler's core or the browser's, which can't run a lint build, the compiler's rules
run alone. The linter tests (`Features/Linters`) run the official linter as a project does, through the package
(`TestSupport.officialLinter`).

## The lint build

A lint build's root is `__lint.nyx` in the linter package's directory (`LintBuilds.rootSource`), which imports the root
module the linter declares and whose `@entry` is its `lint`:

```nanyx fragment
import (
  acme.lint as linter
  nanyx.analysis/analysis as (Report)
  nanyx.analysis/syntax as (Module)
)

@entry
def lint: [Analysis] Module -> list(Report) = { m -> linter.lint(m) }
```

It is compiled as a file of the package, under the linter host (`Package.withHost`), as a test build's root is compiled
under the test host. A root that doesn't compile is no error of the project's: the linter that can't be made fails each
module it would lint, as `<name>/linterFailed`, with the root's first errors, and the build goes on.

## Built once

`LintBuilds.make` keys a lint build by the SHA-256 of what it is built from: the root's source, and each source file of
the linter's package, of the packages it depends on and of the linter host's, by its path and its text. The build is
kept in the package cache, at `.lint/<compiler version>/<sha>.wasm`, so the next process reads it rather than building
it, and each process compiles each lint build once, keeping the function `Load` gives by its key. A linter from the
package index, from GitHub or shipped with the compiler is in a directory that never changes, so each process makes it
once and keeps it, and a keystroke in the editor finds it made: it costs an instance and a call per module. A linter
found by its `path` may be being edited, so a keystroke reads its package under the linter host and its files again to
make the key, tens of milliseconds, and finds the build compiled already unless they changed. `LintBuilds.count` says how many lint builds the process has built, which a test of the
language server counts.

Running it is stage 1a's: `LintAnswers.lintWith` answers the questions the build asks through `nanyx:lint`
(`Nanyx.Runtime.WasmLinter`), from the module's `SemanticModel`, and reads its reports, and `Linters.run` makes them
diagnostics at the rules' configured severities, applies `@allow` and reports a linter that fails.
