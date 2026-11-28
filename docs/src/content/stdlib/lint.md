---
title: lint
description: The official linter, as a package
order: 17.3
---

`lint` is in `nanyx.lint`, the official linter, `default`, which the compiler ships. Its manifest declares every rule the
[Official style linter](../standard-tools/official-linter) page describes. The rules are moving into it from the
compiler a group at a time; until a rule is here, the compiler runs its own copy beside the package. How linters run is
on [Lint builds](../deep-dives/lint-builds).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

The official linter, `default`: the rules its manifest declares, written against
`nanyx.analysis` as any linter is.

Its rules are moving here from the compiler a group at a time. Until a rule is here, the
compiler runs its own copy of it beside this package, so a project that turns the linter on
gets every rule either way.

## `lint`

```nanyx fragment
def lint: [Analysis] Module -> list(Report)
```

The reports of every rule implemented here about a module.

<!-- End of the generated reference. -->
