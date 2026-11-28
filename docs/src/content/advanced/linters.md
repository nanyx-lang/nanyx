---
title: "Linters"
description: "Pluggable diagnostics beyond compiler errors"
order: 10
---

One of the principles of Nanyx is that [the compiler is not a linter](../meta/principles#no-warnings-the-compiler-is-not-a-linter); its focus is to take Nanyx files as input and produce an executable result. 

Thus the compiler itself will only ever emit error-level diagnostics -- things that prevent your program from generating a release build. However, the _compilation process_ supports multiple diagnostic levels beyond errors:

| Level | Description |
|-------|-------------|
| Critical | Compilation cannot proceed |
| Error | A severe problem that prevents a release build from being produced |
| Warning | The code is technically valid but likely incorrect or problematic |
| Info | A suggestion or informational note |
| Hint | A subtle recommendation, often stylistic |

The compiler itself never emits warnings, info, or hints. That responsibility belongs to **linters**.

# What is a linter?

A linter is a pluggable checker that hooks into the compiler pipeline and inspects your code to produce additional diagnostics. This is the same model used by .NET's Roslyn analyzers: the compiler does its job, and linters layer on extra checks that go beyond correctness.

Linters can check for things like:

- Unused variables or imports
- Naming convention violations
- Deprecated API usage
- Performance pitfalls
- Code style preferences

Because linters produce diagnostics through the same infrastructure as the compiler, their output appears inline in your editor alongside any compiler errors -- with the appropriate severity level.

# The default linter

Nanyx ships with a **default linter** that is automatically included in the standard project template. This linter covers common checks that most projects will want, such as flagging unused bindings or warning about a name that hides one the prelude provides. Its rules are described on the [Official style linter](../standard-tools/official-linter) page.

When you create a new project with `nanyx new`, the default linter is already configured and active. You do not need to install or enable it manually.

# Configuring linters

Linters are declared in the `linters` list of the package manifest (the `.nyxp` file, see [Packages and projects](../tour/packages)). Each entry names a linter's package and version, or the directory it is in (see [Writing a linter](#writing-a-linter)), and its `settings` can configure its individual rules. The default linter is the package `nanyx.lint`:

```nanyx fragment
(
  package = (name = "acme.web", version = "1.2.0")
  linters = [
    (
      package = "nanyx.lint"
      version = "0.54.0"
      settings = (rules = (unusedBindings = #warning, shadowedPreludeNames = #info, unusedResults = #warning))
    )
  ]
)
```

A linter runs only if the manifest has an entry for it, so removing the `nanyx.lint` entry uninstalls the default linter. An entry without `settings` runs every rule at its default severity:

```nanyx fragment
linters = [(package = "nanyx.lint", version = "0.54.0")]
```

A single file run on its own (`nanyx run script.nyx`) has no manifest, so it turns its linters on with `::linter` directives at the top of the file; without any, it runs none. `::linter nanyx.lint` turns the official linter on, as its entry without `settings` would, and each `::lint` line under it sets one of its rules' severity, `::lint unusedBindings #error`. A `::lint` line is for the linter of the nearest `::linter` above it. As in a manifest, only the linters the directives name run:

```nanyx
::linter nanyx.lint
::lint unusedBindings #error
::lint nonTailRecursion #off

println("Hello!")
```

`nanyx run` shows only errors and warnings before it runs the program. Info and hint diagnostics are suggestions for code you are working on, so `nanyx check` and the editor show them.

The severities are `#error`, `#warning`, `#info` and `#hint`, in a manifest and in a directive alike. A rule the linter does not declare is an error in the manifest, which names the rules it has, so a misspelt name never silently does nothing. You can suppress a specific rule by setting its severity to `#off`:

```nanyx fragment
settings = (rules = (shadowedPreludeNames = #off))
```

A linter's diagnostics have codes made of the linter's name and the rule's, `default/unusedBindings` for the default linter's rules. To let one definition break a rule rather than turning it off everywhere, write [`@allow`](../tour/attributes#allow) before the definition, naming the rule by that code: `@allow(default/unusedBindings)` (see [Suppressing a rule in one place](../standard-tools/official-linter#suppressing-a-rule-in-one-place)).

Because severity is configured per project, a team that wants strictness can promote a linter rule to `#error`, which then blocks a release build exactly as a compiler error would. This is how "warnings as errors" works in Nanyx: it is a project choice, not a compiler setting.

# Writing a linter

A linter is a Nanyx package. Its manifest declares that it is a linter, with the module that exports its `lint`, its name, and its rules, and it depends on `nanyx.analysis`, the package every linter is written against, at the compiler's version:

```nanyx fragment
(
  package = (
    name = "acme.lint"
    version = "1.0.0"
    sources = ["src/**/*.nyx"]
    linter = (
      root = "acme.lint"
      name = "acme"
      rules = [
        (
          id = "shortNames"
          title = "Short name"
          severity = #warning
          description = "A top-level definition whose name is shorter than three letters."
        )
      ]
    )
  )
  dependencies = [(package = "nanyx.analysis", version = "0.54.0")]
)
```

Name the package for what it checks, with `.lint` at the end, as `acme.lint`. Its `name` is the first part of its rules' codes, so the rule above reports as `acme/shortNames`, and `@allow(acme/shortNames)` suppresses it. Each rule has an `id`, in camelCase, a `title`, which heads each diagnostic it reports, a default `severity`, and a `description`; `unnecessary = true` says that what it reports is code that does nothing, which the editor draws faded. Because the rules are in the manifest, the compiler checks a project's `settings` and `@allow` against them without running the linter.

The root module exports `lint`, a function from one module to the reports about it. The module is its syntax tree, as written, from `nanyx.analysis`'s [`syntax`](../stdlib/syntax) module; what only type checking knows, such as the type of an expression or what a name refers to, it asks the [`Analysis`](../stdlib/analysis) context:

```nanyx fragment
export module acme.lint

import (
  nanyx.analysis/analysis as (Analysis, Report)
  nanyx.analysis/syntax as (Module, TopLevelItem)
)

def check: TopLevelItem -> list(Report) = { item ->
  match item.kind
    | #def(d) ->
      if string.length(d.name) < 3 then
        [(rule = "shortNames", span = item.span, message = "'{d.name}' is short", subject = #some(d.name), related = [], fixes = [])]
      else
        []
    | _ -> []
}

export def lint: [Analysis] Module -> list(Report) = { m -> list.flatMap(m, check) }
```

A report names its rule by its `id`. It doesn't choose a severity: the compiler gives it the one the project configures, or the rule's default. A linter that crashes, takes too long, or reports a rule it doesn't declare is one `acme/linterFailed` warning for the module, and the module still compiles.

A project runs the linter by naming it in its `linters`, by its directory while you work on it, or by its version once it is published, and configures its rules as it does the default linter's:

```nanyx fragment
linters = [
  (package = "nanyx.lint", version = "0.54.0")
  (package = "acme.lint", path = "../acme.lint", settings = (rules = (shortNames = #error)))
]
```

`nanyx check`, `build`, `run`, `test` and the editor all run it. The compiler builds the linter once, and keeps the build in the package cache until the linter's files or the compiler change, so typing in the editor doesn't build it again. A linter written against another version of `nanyx.analysis` than the compiler's can't run, and is an error at its entry. A script's `::linter` can name only the default linter for now. How a linter package is built and run is on [Lint builds](../deep-dives/lint-builds), and what is still to come on [Linters: compiler design](../work-in-progress/linters).

# Why separate linters from the compiler?

Keeping the compiler free of warnings and style checks has several benefits:

- **Predictability** -- if the compiler accepts your code, it will produce correct output. There is no ambiguity about what constitutes an error versus a suggestion.
- **Speed** -- the compiler can focus on type-checking and code generation without running additional analysis passes.
- **Flexibility** -- teams can choose exactly which checks they want, at what severity, without affecting the core compilation process.
- **Upgradability** -- linters can be updated independently of the compiler, and new rules can be added without changing compiler behavior.
