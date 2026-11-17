---
title: "Refactorings"
description: "Planned editor tools for changing programs safely, and the rules every refactoring follows"
order: 32
---

> The refactorings built so far are described for users on
> [VS Code extension](../standard-tools/vs-code-extension#refactorings). This page keeps the rules every refactoring
> follows, and the [planned](#planned) ones.

Nanyx should have the best editing experience we can manage, and that means the editor giving people the tools to
change a program without breaking it. This workstream is those tools: rename, expand and shrink selection, extract
and inline, and the small rewrites that save typing. Its issues carry the
[`refactoring`](https://github.com/nanyx-lang/nanyx/labels/refactoring) label and the Refactoring stream on the
[project](https://github.com/orgs/nanyx-lang/projects/1).

# How a refactoring is built

- **In the compiler, not the server.** A refactoring is a function in the compiler's `Refactorings/` folder, one file
  each, in the namespace `NyxCompiler.Refactorings`, from a checked module (or the parser's syntax tree, for one that
  needs no types) and a position or selection to a list of `TextEdit`s, as a diagnostic's `Fix` already is
  (`Diagnostics.Fix`). `Refactoring.fs` holds what they share, and its `RefactoringText` module what the ones that
  write code share. The language server's `CodeActionHandler` only lists what's available at the selection, a refused
  one disabled with its reason. So every refactoring is tested in F#, in `Features/Refactorings/` of the compiler's
  tests, without an editor, and could be offered from the CLI too.
- **It never changes what the program means.** Each refactoring states when it's refused, and refuses with a reason
  rather than producing code that type-checks differently: a name that would be shadowed, a cell assigned in between,
  a workflow keyword whose meaning depends on its block. Where it can't tell from the syntax, it compiles its result
  and refuses if there are new errors or a name refers to something else. Its tests include the refusals.
- **Its output is formatted.** Edits are laid out as the formatter would lay out the result
  (`RangeFormatting.formatRange`), so applying a refactoring never leaves code `nanyx format` would change.
- **Code action kinds** are the standard ones, so editors put them in the usual menus: `refactor.extract` (introduce
  local and introduce function), `refactor.inline`, `refactor.rewrite` (write the inferred type, call and pipe), and
  `source.organizeImports`. Rename and selection ranges have their own LSP requests.
- **A new name starts a rename.** A refactoring that invents a name (introduce local, introduce function) carries the
  command `nanyx.renameAt`, which the VS Code extension registers: after the edits, it puts the cursor on the name and
  starts a rename.

Each refactoring that compiles its result does so for every code action request it is offered on, which can be slow
on a large file; [#1846](https://github.com/nanyx-lang/nanyx/issues/1846) moves that work out of the request.

# Planned

Each has an issue with its design; none is built yet.

| Refactoring | Issue |
|---|---|
| Introduce a type alias from a written type | [#1819](https://github.com/nanyx-lang/nanyx/issues/1819) |
| Inline a type alias | [#1826](https://github.com/nanyx-lang/nanyx/issues/1826) |
| Write the inferred types of the `_`s in an annotation | [#1820](https://github.com/nanyx-lang/nanyx/issues/1820) |
| Change a def's signature: add, remove or reorder parameters, updating every call | [#1822](https://github.com/nanyx-lang/nanyx/issues/1822) |
| Make a parameter named or positional, updating every call | [#1827](https://github.com/nanyx-lang/nanyx/issues/1827) |
| Move a def to another module | [#1823](https://github.com/nanyx-lang/nanyx/issues/1823) |
| Convert a match to an `if` and back | [#1824](https://github.com/nanyx-lang/nanyx/issues/1824) |
| Make a structural type protected or private, with a constructor | [#1825](https://github.com/nanyx-lang/nanyx/issues/1825) |
| Turn a `++` chain into an interpolated string, and back | [#1828](https://github.com/nanyx-lang/nanyx/issues/1828) |
| Implement a context for a type: stubs for its missing attached definitions | [#1829](https://github.com/nanyx-lang/nanyx/issues/1829) |
| Convert a lambda to its shorthand and back | [#1830](https://github.com/nanyx-lang/nanyx/issues/1830) |
| Move a file and update the imports of it by path | [#1831](https://github.com/nanyx-lang/nanyx/issues/1831) |
| Rename a `module name =` block | [#1832](https://github.com/nanyx-lang/nanyx/issues/1832) |
