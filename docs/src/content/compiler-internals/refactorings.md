---
title: "Refactorings"
description: "How a refactoring is built, and the rules every refactoring follows"
order: 14
---

The refactorings built so far are described for users on
[VS Code extension](../standard-tools/vs-code-extension#refactorings). This page says how a refactoring is built, and the
rules every refactoring follows. The planned ones are on [Refactorings](../work-in-progress/refactorings) in Work in
progress.

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
