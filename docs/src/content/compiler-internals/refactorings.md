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
- **Compiling waits until it is picked.** A refactoring that compiles its result offers it as Offer.Deferred: its
  title and kind, and a function that compiles and gives the Available or Refused offer. Editors ask for code
  actions on every move of the cursor, so CodeActionHandler lists a deferred one without its edits and decides it in
  codeAction/resolve, when the user picks it: a request compiles nothing. Refused then, its reason is shown as a
  message rather than in the menu. A client that can't resolve an action's edits gets them decided in the request. The
  formatter's settings are read once per text of the document (OpenDocument.RefactoringOptions). Tests decide every
  offer with Offer.decided.
- **Its output is formatted.** Edits are laid out as the formatter would lay out the result
  (`RangeFormatting.formatRange`), so applying a refactoring never leaves code `nanyx format` would change.
- **Code action kinds** are the standard ones, so editors put them in the usual menus: `refactor.extract` (introduce
  local, introduce function and introduce type alias), `refactor.inline` (inline a def or a type alias), `refactor.rewrite` (write the inferred type, write the types of the `_`s, call and pipe, shorthand lambdas), and
  `source.organizeImports`. Rename and selection ranges have their own LSP requests.
- **A new name starts a rename.** A refactoring that invents a name (introduce local, introduce function, introduce type alias, a shorthand lambda's parameters) carries the
  command `nanyx.renameAt`, which the VS Code extension registers: after the edits, it puts the cursor on the name and
  starts a rename.
