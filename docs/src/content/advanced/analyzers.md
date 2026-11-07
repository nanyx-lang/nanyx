---
title: "Analyzers"
description: "Pluggable diagnostics beyond compiler errors"
order: 10
---

One of the principles of Nanyx is that [the compiler is not a linter](../foundations/principles#no-warnings-the-compiler-is-not-a-linter); its focus is to take Nanyx files as input and produce an executable result. 

Thus the compiler itself will only ever emit error-level diagnostics -- things that prevent your program from generating a release build. However, the _compilation process_ supports multiple diagnostic levels beyond errors:

| Level | Description |
|-------|-------------|
| Critical | Compilation cannot proceed |
| Error | A severe problem that prevents a release build from being produced |
| Warning | The code is technically valid but likely incorrect or problematic |
| Info | A suggestion or informational note |
| Hint | A subtle recommendation, often stylistic |

The compiler itself never emits warnings, info, or hints. That responsibility belongs to **analyzers**.

# What is an analyzer?

An analyzer is a pluggable Nanyx module that hooks into the compiler pipeline and inspects your code to produce additional diagnostics. This is the same model used by .NET's Roslyn analyzers: the compiler does its job, and analyzers layer on extra checks that go beyond correctness.

Analyzers can check for things like:

- Unused variables or imports
- Naming convention violations
- Deprecated API usage
- Performance pitfalls
- Code style preferences

Because analyzers produce diagnostics through the same infrastructure as the compiler, their output appears inline in your editor alongside any compiler errors -- with the appropriate severity level.

# The default analyzer

Nanyx ships with a **default analyzer** that is automatically included in the standard project template. This analyzer covers common checks that most projects will want, such as flagging unused bindings or warning about shadowed names.

When you create a new project with `nanyx new`, the default analyzer is already configured and active. You do not need to install or enable it manually.

# Configuring analyzers

Analyzers are declared in the package manifest (the `.nyxpack` file, see [Packages and projects](../tour/packages)). Each analyzer can be enabled, disabled, or have its individual rules configured:

```toml
[analyzers.default]
unusedBindings = "warning"
shadowedNames = "info"
deprecatedUsage = "warning"
```

You can suppress a specific rule by setting its severity to `"none"`:

```toml
[analyzers.default]
shadowedNames = "none"
```

Because severity is configured per project, a team that wants strictness can promote an analyzer rule to `"error"`, which then blocks a release build exactly as a compiler error would. This is how "warnings as errors" works in Nanyx: it is a project choice, not a compiler setting.

# Writing custom analyzers

Because analyzers are just modules that conform to the analyzer interface, you can write your own. A custom analyzer receives the typed syntax tree and returns a list of diagnostics:

```nanyx
module myAnalyzer

def analyze: TypedAst -> list(Diagnostic) = { ast ->
  -- Inspect the AST and collect diagnostics
  ast.declarations
    \filter { d -> (d.name \startsWith("_")) and d.isPublic }
    \map { d -> Diagnostic(
      severity = #warning
      message = "Public definitions should not start with an underscore"
      location = d.location
    )}
}
```

Custom analyzers can be distributed as packages and added to any project.

# Why separate analyzers from the compiler?

Keeping the compiler free of warnings and style checks has several benefits:

- **Predictability** -- if the compiler accepts your code, it will produce correct output. There is no ambiguity about what constitutes an error versus a suggestion.
- **Speed** -- the compiler can focus on type-checking and code generation without running additional analysis passes.
- **Flexibility** -- teams can choose exactly which checks they want, at what severity, without affecting the core compilation process.
- **Upgradability** -- analyzers can be updated independently of the compiler, and new rules can be added without changing compiler behavior.
