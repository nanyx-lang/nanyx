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
Expand and shrink selection, writing the inferred type, introducing a local or a function are done, and rename within a
module.

Refactorings live in the compiler's `Refactorings/` folder, one file each, after the editor features they build on
(`Outline.fs`), in the namespace `NyxCompiler.Refactorings`. Their tests are in `Features/Refactorings/` of the
compiler's tests. `Refactoring.fs` holds what they share: a `Refactoring` is a title, a `RefactoringKind` (the code
action kind), its edits and where a name it made up is in the text they make, and an `Offer` is one that is `Available`
or `Refused` with its reason. Its `RefactoringText` module has what the ones that write code share: making edits and
formatting the result, where a def goes, which names are taken, and whether a recompiled text has new errors. The language
server's `CodeActionHandler` lists the offers at the start of the requested range as code actions, a refused one
disabled with its reason, so the editor shows why; selection ranges have a handler of their own. A refactoring
that makes up a name carries the command `nanyx.renameAt`, which the VS Code extension registers: after the edits, it
puts the cursor on the name and starts a rename.

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

## Write the inferred type

`WriteInferredType.at` is offered on the name of a def, top-level or local, or a lambda parameter that writes no
type. It writes the annotation the type hints carry (`TypeHints`), so the code action and double-clicking an inlay hint
write the same thing: `Source` notation, the contexts first (`def add: [Add(a)] a, a -> a`), a function parameter's
type bracketed, and after the contexts where the def writes only those. Each type and context is named as the file
writes it (`Signatures.writable`): an alias keeps its name, and a type imported under an alias is written through it
(`s.Circle`). The official linter's `unannotatedExports` fix writes the same. It is refused on a pattern variable, a
name a destructuring def binds, and a type with a part that failed to check. Its tests compile the result again.

## Introduce local

`IntroduceLocal.at` is offered where the selection, less the spaces around it, is exactly an expression. The def goes
before the statement of the innermost block that holds it, above the comments just before that statement. Outside any
block it is a top-level def before the item, above its attributes and doc comment. In a lambda whose body is one
expression, such as `xs \map { x -> x * 2 + 1 }`, the body is made a block for it. The name is the field the
expression reads or the function it calls, unless that is taken, and otherwise `result`, numbered to be unique: it
is never a name in scope or one any expression of the top-level item uses, since a local named after the function it
calls would hide it. A second offer replaces every later occurrence in the block that is written the same and whose
names resolve to the same symbols; it is left out when the expression calls a function that requires a context, a
context's member, or touches a cell, since each occurrence could give something different.

It is refused where the expression runs only in some cases (a branch of an `if`, a match arm, the right of `and` or
`or`, a lambda written around it by the parser, as the right of `??` is), where it uses `try` or `return`, and
where it reads a cell assigned earlier in the statement.

Its output is formatted: the compiler references the formatter, and `RangeFormatting.formatRange` lays out the
top-level items the edit touches, with the document's formatter settings, and the refactoring is the one edit from
the old text to that. Its tests check the result compiles and is formatted. The new name starts a rename.

## Introduce function

`IntroduceFunction.at` is offered where the selection, less the spaces around it, is exactly an expression or a run of
whole statements of a block (a run with a def in it wins over the expression that is a def's value, as `mut x = 0`'s
is). It makes two offers: a top-level def after the item that holds the selection, and a local function before the
statement that holds it, when that statement starts its line. The new name is `extracted`, numbered to be unique as
introduce local's is, and starts a rename.

- **Parameters** are the locals, parameters and pattern variables it uses that are declared outside it, in the order
  they are first used. A cell is passed as one (`{ mut count -> ... }`, called `extracted(mut count)`), so the
  function reads and changes the cell itself; this covers a cell only read, too, which a lambda in the selection
  might read later.
- **Result:** an expression's value. Statements give the locals they define that the statements after them in the
  block use: one is defined again by the call (`def doubled = extracted(w)`), several as a tuple
  (`def (a, b) = extracted(w)`); with none the call is a statement, and statements ending in a def end with `()`.
- **Type:** the text is compiled with the def written without a type, and the type it was inferred is written as
  Write the inferred type writes it, contexts included (`[ToString(a), Console] a -> ()`). If the text with the type
  written does not compile, the def is left without one. The offer is refused if the text does not compile at all,
  with the first new error. `at` takes the function that compiles a text, as `Rename.edits` does; the server compiles
  the document's text as it does when it changes.
- **Refused** where the selection uses `try` or `return`, where statements have a `use`, an import or a type definition
  (the statements after them may need it), where they declare a cell the statements after them use, and where a
  selection inside a def is not a whole expression or whole statements.

Its tests compile the result and check it is formatted. Each code action request with such a selection compiles the
document two or three times more; if that is slow on large files, the work can move to `codeAction/resolve`.

## Rename

`Rename.prepare` finds the name at a position (or just before it, where an editor's cursor often is) and its own part,
`shout` in `string.shout`, or refuses it with a reason; `Rename.edits` renames its declaration and every use of it
in the module (`Definition.referencesTo`). An attached definition keeps its type's part. A local, a parameter, a
pattern variable, a top-level def and a type can be renamed. The new name must be a name, not a keyword, not one of
the compiler's (`__`), and, for a type, not a one-letter lowercase name, which would be a type variable.

It never changes what the program means: the renamed text is compiled (the server compiles it as it compiles the
document), and the rename is refused if that has more errors, such as a name defined twice, or if any name in it refers
to something other than it did before (`Definition.referenceSites`, compared with each offset moved by the edits).
That covers a new name that would hide another at a use and one that another would hide, and names the use.

The server answers `textDocument/prepareRename` and `textDocument/rename`; a refusal is a `RequestFailed` error
whose message is the reason, which VS Code shows.

Not yet: names declared in another module, exported names (other modules may use them, and the server can't find
those uses until it can search the workspace, [#877](https://github.com/nanyx-lang/nanyx/issues/877)), an import's
alias, a context's members, record fields, and modules. Renaming a type does not yet rename its attached definitions'
prefixes.

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
  local and introduce function), `refactor.inline`, `refactor.rewrite` (write the inferred type, call and pipe), and
  `source.organizeImports`. Rename and selection ranges have their own LSP requests.
- **A new name starts a rename.** A refactoring that invents a name (introduce local, introduce function) leaves the
  cursor on it, ready to rename.

# Starter issues

| Refactoring | Issue |
|---|---|
| Rename a symbol (within a module) | [#1534](https://github.com/nanyx-lang/nanyx/issues/1534) |
| Expand and shrink selection (done) | [#1535](https://github.com/nanyx-lang/nanyx/issues/1535) |
| Introduce local (done) | [#1536](https://github.com/nanyx-lang/nanyx/issues/1536) |
| Introduce function (done) | [#1537](https://github.com/nanyx-lang/nanyx/issues/1537) |
| Inline a local or a function | [#1538](https://github.com/nanyx-lang/nanyx/issues/1538) |
| Write the inferred type (done) | [#1539](https://github.com/nanyx-lang/nanyx/issues/1539) |
| Convert between a call and a pipe | [#1540](https://github.com/nanyx-lang/nanyx/issues/1540) |
| Organise imports | [#1541](https://github.com/nanyx-lang/nanyx/issues/1541) |

Rename across files depends on finding references across the package's files
([#1556](https://github.com/nanyx-lang/nanyx/issues/1556)), which waits for the language server to resolve packages
([#866](https://github.com/nanyx-lang/nanyx/issues/866)).

# Ideas for later

- Change a def's signature: add, remove or reorder parameters, updating every call.
- Move a def to another module, updating imports.
- Convert a match to an `if` and back; add the missing arms of a match.
- Extract a type alias from a written type; turn a structural type into a protected one.
- Rename a module or file and update every import.
