---
title: "Refactorings"
description: "Editor tools for changing programs safely: rename, extract, inline, selection and more"
order: 32
---

> The refactorings below are built, and described for users on
> [VS Code extension](../standard-tools/vs-code-extension#refactorings). This page keeps how each is built, the rules
> every refactoring follows, and the [ideas for later](#ideas-for-later).

Nanyx should have the best editing experience we can manage, and that means the editor giving people the tools to
change a program without breaking it. This workstream is those tools: rename, expand and shrink selection, extract
and inline, and the small rewrites that save typing. Its issues carry the
[`refactoring`](https://github.com/nanyx-lang/nanyx/labels/refactoring) label and the Refactoring stream on the
[project](https://github.com/orgs/nanyx-lang/projects/1).

# How they are built

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

## Inline

`Inline.at` is offered on the name of a top-level or local def of the module, or a use of it: "Inline `x`" replaces
every use and removes the def, with its doc comment and attributes and the blank lines after it, and on a use "Inline
this use of `x`" replaces only that one. A def that is exported is inlined at a use only, never removed.

- **A value** that is not a lambda is put in for each use; where it may need brackets (an operator, a pipe, a branch,
  a lambda) it is written in them, and the formatter drops those it doesn't need. A use that calls it is inlined only
  when the value is a name.
- **A function**, a def whose value is a lambda written `{ ... }`: a call becomes its body. An argument that is a name
  or a literal, or whose parameter the body uses once and not as a function, is put in for the parameter; any other is
  bound by a def first, so nothing is evaluated more or fewer times than before. Those defs, and the statements of a
  body that is a block, go before the statement that holds the call. A use that does not call it becomes the lambda.
- **Refused** where the def is recursive, is a cell or reads one, or (a value only) calls a function that requires a
  context, which inlining would run at another time; where a name its value or body uses would refer to something else
  at a use (the innermost symbol of that name whose scope holds the use, `typed.Scopes`); where an argument put in would
  be taken by a name the body declares, or a def it puts before the statement would hide or clash with a name of the
  item; where the call is in a branch or a lambda and statements would have to go first; for a call with named
  arguments, the wrong number of arguments, or a cell parameter; and where the body uses `try` or `return`. Finally the
  inlined text is compiled (`at` takes the function that compiles a text, as `IntroduceFunction.at` does), and refused
  with its first new error if it would not compile.

Like introduce function, it compiles the document again for each offer, here whenever the cursor is on a def's name.

## Convert between a call and a pipe

`CallAndPipe.at` is offered on the name of a call the user wrote that finds its function: "Convert to pipe" writes
`f(x, y)` as `x \f(y)`, and `f(x)` as `x \f`; on a pipe's name, "Convert to call" writes it back. A chain converts as a
whole, from the outermost call (or pipe) that takes its first argument (or value) from the one at the cursor:
`list.filter(list.map(xs, f), g)` becomes `xs \map(f) \filter(g)`. A value that may need brackets gets them, and a
lambda that is the only other argument is written after the name, as a trailing lambda is. Calls with named arguments
are left alone.

Names: a dotted name is piped by its own part first, `s \trim` for `string.trim(s)`, which finds the definition
attached to the value's type; converting to a call keeps a pipe's name first, and then writes each by the name of the
definition it found (`string.trim(s)` for `s \trim`). Each choice is tried on the whole chain: the text is compiled, and
taken only if it has no new errors and every name in the module refers to what it did (compared by each symbol's name,
kind and file, since a compile makes up symbols of its own). So where the own name would find another definition, the
whole name is kept (`s \text.trim`); where neither naming means the same, it is refused.

## Organise imports

`OrganiseImports.at` takes the module's top-level imports, drops each that nothing uses (its symbol is in no
`ImportReferences`, as the `unusedImports` rule finds it; an alias starting with `_` is kept, as the rule keeps it) and
each name of a selection nothing names (`as (abs, max)` to `as (abs)`), and writes the rest as one import where the
first import was, sorted by the formatter's `ImportOrder`: the standard library, then packages, then imports by path.
The other import statements are removed with their lines. It is offered only when that changes the formatted text,
and it is refused where the imports have comments among them, which it would lose, or where the result would not
compile. Imports in a `module name =` block and re-exports are left alone.

The server offers it as `source.organizeImports`, which VS Code runs on save when `editor.codeActionsOnSave` asks
for it, and as a quick fix of each `unusedImports` diagnostic. The handler computes only the kinds a request asks for
(`context.only`), so a save that asks for organising imports runs nothing else.

## Rename

`Rename.prepare` finds the name at a position (or just before it, where an editor's cursor often is) and its own part,
`shout` in `string.shout`, or refuses it with a reason; `Rename.editsIn` renames its declaration and every use of it
in its scope (`Rename.Scope`): every source file of the package, for a file in one, or else the module alone. Uses in
other files are found as find references finds them ([#1556](https://github.com/nanyx-lang/nanyx/issues/1556)): each
file's compile is searched for the same symbol (`Definition.sameSymbol`). An attached definition keeps its type's part,
and renaming a type renames that part of its attached definitions' names (`Point` in `def Point.show` and in
`Point.show(p)`). A local, a parameter, a pattern variable, a top-level def (exported or not, in a package), a type,
an import's alias (also where a type is written under it, `s.Point`), and, as below, a context's member, a record's
field and a module can be renamed. An import with no alias is
refused, as is an exported name in a file in no package, since other files may import it by path. The new name must
be a name, not a keyword, not one of the compiler's (`__`), and, for a type, not a one-letter lowercase name, which
would be a type variable.

It never changes what the program means: the renamed files are compiled (the server compiles them as it compiles the
document, or the package), and the rename is refused if one has more errors, such as a name defined twice, or if any
name in them refers to something other than it did before (`Definition.referenceSites`, compared with each offset moved
by the edits in its file). That covers a new name that would hide another at a use and one that another would hide,
and names the use, with its file when it is in another one.

Three kinds of name are written in more places than the checker records as references
([#1639](https://github.com/nanyx-lang/nanyx/issues/1639)); `FieldSites` and `ModuleSites` find them:

- **A context's member** is a symbol, renamed at its declaration and uses as any other, and also where an instance of
  its context is written: the named arguments of a call of the context's constructor (`use Log(log = ...)`) and the
  record a def typed as the context gives (`use Show(int)(show = ...)`). A rename can start at any of them.
- **A record's field** has no symbol. Its sites are each record type's fields (not a context's), each record written in
  an expression (a constructor's arguments, a call's named arguments, the fields `with` updates and those beside a
  spread are the whole record's), each field read, and the patterns of `match` and `except`. Each site's record comes
  from the types the checker inferred there (`FieldSites.recordOf`): a protected or private type's by its identity, any
  other by the names of its fields, since two structural records with the same names are one type. The field is renamed
  at every site of the same record and nowhere else. A field of that name read from a record whose type is not known,
  an open record or a type variable (`{ r -> r.x }`), refuses the rename, naming the line, since it may be either; so
  does starting at one. A tuple pattern that takes the field apart by name alone, `(x, y)`, is written by member,
  `(across = x, y = y)`, as the two can't be mixed.
- **A module** is renamed from its `module` line or from an import of it (`import shapes`, `import app/shapes`): its
  `module` line, each import of it in the package and each import of its file by path. Its file is renamed with it
  where the file is named for the module and no file of the new name is there (`Renaming.Files`). It is refused for a
  file in no package. To compile the renamed package, `Package.compilePackageReading` reads the module each file
  declares from the text it is given (`PackageModules.reading`), not from disk.

The server answers `textDocument/prepareRename` and `textDocument/rename`; a refusal is a `RequestFailed` error
whose message is the reason, which VS Code shows. The edit is a `WorkspaceEdit` with the changes to each file, and the
files a module's rename renames as `RenameFile` operations after them, where the client can rename files.

Not yet: a `module name =` block can't be renamed.

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

# Ideas for later

- Rename a `module name =` block.

- Change a def's signature: add, remove or reorder parameters, updating every call.
- Move a def to another module, updating imports.
- Convert a match to an `if` and back; add the missing arms of a match.
- Extract a type alias from a written type; turn a structural type into a protected one.
- Move a file and update the imports of it by path.
