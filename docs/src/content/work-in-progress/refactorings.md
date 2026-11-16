---
title: "Refactorings"
description: "Editor tools for changing programs safely: rename, extract, inline, selection and more"
order: 32
---

Nanyx should have the best editing experience we can manage, and that means the editor giving people the tools to
change a program without breaking it. This workstream is those tools: rename, expand and shrink selection, extract
and inline, and the small rewrites that save typing. Its issues carry the
[`refactoring`](https://github.com/nanyx-lang/nanyx/labels/refactoring) label and the Refactoring stream on the
[project](https://github.com/orgs/nanyx-lang/projects/1).

# Where we are

The language server offers go to definition, find references within a document, hover, inlay hints, completion,
semantic tokens, formatting, and quick fixes from diagnostics ([Language server and editor](./language-server)).
Expand and shrink selection is done. There is no rename yet, and code actions are quick fixes only.

Refactorings live in the compiler's `Refactorings/` folder, one file each, after the editor features they build on
(`Outline.fs`), in the namespace `NyxCompiler.Refactorings`. Their tests are in `Features/Refactorings/` of the
compiler's tests, and the language server's handler for each in `Handlers/`.

## Expand and shrink selection

`SelectionRanges.at` parses the text and gives the spans around a position, smallest first, and the server's
`textDocument/selectionRange` links them up. The chain is every node whose span holds the position, together with
the groups of nodes that are not nodes of their own: a call's arguments, a lambda's parameters, a record's fields, a
named field (`x = 0`, from its name, which the parser records as a declared name, to its value), a match arm (from its
first pattern to its body) and its patterns, and the call a pipe makes (`map(f)` in `xs \map(f)`). The spans are
sorted by size and each is kept only if it holds the one before and is larger, so a node that spans exactly what its
child spans is left out, and nodes the compiler made up, whose spans can overlap, never break the chain. Module
blocks come from the parser's list of them, and the whole file ends the chain. Comments and whitespace belong to no
node, so at one the chain starts at the smallest node around it.

# How a refactoring is built

- **In the compiler, not the server.** A refactoring is a function in the compiler from a checked module (or the
  parser's syntax tree, for one that needs no types) and a position or selection to a list of `TextEdit`s, as a
  diagnostic's `Fix` already is (`Diagnostics.Fix`). The language server only lists what's available at the
  selection and applies the edits. So every refactoring is tested in F#, without an editor, and could be offered
  from the CLI too.
- **It never changes what the program means.** Each refactoring states when it's refused, and refuses with a reason
  rather than producing code that type-checks differently: a name that would be shadowed, a cell assigned in between,
  a workflow keyword whose meaning depends on its block. Its tests include the refusals.
- **Its output is formatted.** Edits are laid out as the formatter would lay out the result, so applying a
  refactoring never leaves code `nanyx format` would change.
- **Code action kinds** are the standard ones, so editors put them in the usual menus: `refactor.extract` (introduce
  local, introduce function), `refactor.inline`, `refactor.rewrite` (write the inferred type, call and pipe), and
  `source.organizeImports`. Rename and selection ranges have their own LSP requests.
- **A new name starts a rename.** A refactoring that invents a name (introduce local, introduce function) leaves the
  cursor on it, ready to rename.

# Starter issues

| Refactoring | Issue |
|---|---|
| Rename a symbol | [#1534](https://github.com/nanyx-lang/nanyx/issues/1534) |
| Expand and shrink selection (done) | [#1535](https://github.com/nanyx-lang/nanyx/issues/1535) |
| Introduce local | [#1536](https://github.com/nanyx-lang/nanyx/issues/1536) |
| Introduce function | [#1537](https://github.com/nanyx-lang/nanyx/issues/1537) |
| Inline a local or a function | [#1538](https://github.com/nanyx-lang/nanyx/issues/1538) |
| Write the inferred type | [#1539](https://github.com/nanyx-lang/nanyx/issues/1539) |
| Convert between a call and a pipe | [#1540](https://github.com/nanyx-lang/nanyx/issues/1540) |
| Organise imports | [#1541](https://github.com/nanyx-lang/nanyx/issues/1541) |

Rename across files depends on
finding uses across the workspace ([#877](https://github.com/nanyx-lang/nanyx/issues/877), which needs a decision).

# Ideas for later

- Change a def's signature: add, remove or reorder parameters, updating every call.
- Move a def to another module, updating imports.
- Convert a match to an `if` and back; add the missing arms of a match.
- Extract a type alias from a written type; turn a structural type into a protected one.
- Rename a module or file and update every import.
