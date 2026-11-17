---
title: "Refactorings"
description: "Planned editor tools for changing programs safely"
order: 32
---

> The refactorings built so far are described for users on
> [VS Code extension](../standard-tools/vs-code-extension#refactorings), and how a refactoring is built, with the rules
> every refactoring follows, in [Refactorings](../compiler-internals/refactorings) in the Compiler internals section.
> This page keeps the [planned](#planned) ones.

Nanyx should have the best editing experience we can manage, and that means the editor giving people the tools to
change a program without breaking it. This workstream is those tools: rename, expand and shrink selection, extract
and inline, and the small rewrites that save typing. Its issues carry the
[`refactoring`](https://github.com/nanyx-lang/nanyx/labels/refactoring) label and the Refactoring stream on the
[project](https://github.com/orgs/nanyx-lang/projects/1).

# Planned

Each refactoring below has an issue with its design; none is built yet.

| Refactoring | Issue |
|---|---|
| Change a def's signature: add, remove or reorder parameters, updating every call | [#1822](https://github.com/nanyx-lang/nanyx/issues/1822) |
| Make a parameter named or positional, updating every call | [#1827](https://github.com/nanyx-lang/nanyx/issues/1827) |
| Move a def to another module | [#1823](https://github.com/nanyx-lang/nanyx/issues/1823) |
| Convert a match to an `if` and back | [#1824](https://github.com/nanyx-lang/nanyx/issues/1824) |
| Make a structural type protected or private, with a constructor | [#1825](https://github.com/nanyx-lang/nanyx/issues/1825) |
| Turn a `++` chain into an interpolated string, and back | [#1828](https://github.com/nanyx-lang/nanyx/issues/1828) |
| Implement a context for a type: stubs for its missing attached definitions | [#1829](https://github.com/nanyx-lang/nanyx/issues/1829) |
| Move a file and update the imports of it by path | [#1831](https://github.com/nanyx-lang/nanyx/issues/1831) |
| Rename a `module name =` block | [#1832](https://github.com/nanyx-lang/nanyx/issues/1832) |
