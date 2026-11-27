---
title: "VS Code extension"
description: "Installing and configuring the Nanyx extension for Visual Studio Code"
order: 3
---

The Nanyx extension for Visual Studio Code highlights `.nyx` files and package manifests, `.nyxp` files, which are Nanyx too (the type in a definition attached to a built-in type, `def string.shout`, is coloured as a type, the escapes in a double-quoted string, such as `\n` and `\{`, are coloured as escapes, a [doc comment](../getting-started/basics#doc-comments) is coloured as one throughout its lines, only [Nanyx's operators](../tour/operators#the-operators) are coloured as operators, every word the language reserves is coloured as a keyword (`pattern`, which declares a [custom pattern](../advanced/custom-patterns), among them), as is `scoped` before `context`, and a script's [directives](../getting-started/files-and-projects#directives), such as `::install acme.util ../util`, have their name coloured as a keyword and their package names, versions, directories, tags and numbers each coloured as what they are) and runs the compiler as a language server (`nanyx lsp`), which gives you:

- errors and warnings as you type, each shown as its headline (`Missing context 'Console'`) with the details on the
  next line, and each compiler error's code (such as `NYX0201`) a link to its section of [Compiler errors](./errors). An error in a file the document imports is shown in that file, and the import that
  brings the file in gets one diagnostic linking to each of its errors. A change to a file, in the editor or on disk,
  checks again the open files that import it. Code the [official linter](./official-linter) reports as unused or as
  never running, such as an unused local, parameter or import, an unused private definition, code after a `crash` or
  `return`, or a match arm that can never match, is drawn faint as well as underlined; an export never is
- every name coloured by [what it is](#semantic-highlighting): a function, a parameter, a variable, a field, a tag, a type, a context or a module, as your theme colours other languages' names, the names of [nominal types](#nominal-types) in italics, and a name that names nothing in scope as an error
- the inferred type of whatever is under the pointer, on hover, after the name on a name (`def name: [Console] string -> ()`, `n: int`), with the doc comment of the name there. On a type's name it shows the type's definition, and for a nominal type whether it is protected or private and which module declares it. On a module's name, in its `module` line, an import (its path or its alias) or an alias that qualifies a name (`m` in `m.foo`), hover shows the module's doc comment, the `--|` comment before its `module` line. On a [dimension](../advanced/units-of-measure), plain or derived, in a type or its own declaration, it shows the dimension's declaration and the units that make values of it, from the smallest to the largest, the default storage and display units marked, with how each is declared, as `ms = 1_000us` or `kmh = 1km / 1hr`; so does a measured value's type, as `def timeout: Time(int)`. On the unit of a number with a unit, `ms` in `500ms`, or a unit's declaration, it shows the unit's declaration, how many of its dimension's default storage units it is, and the dimension's units. Either ends with its doc comment, as a hover on any name does. On a built-in type's name in a type, such as `int`, `I64`, `char` or `List`, it says what the type is, with a link to its docs page, and its other name where it has one: `int` is another name for `I64`, `float` for `F64`, and `List` is `list`. On a `_` in a type, as in `def xs: list(_) = [1, 2]`, it shows what the `_` was inferred to be, and on an operator that calls a definition, such as a `+` a type defines, it shows that definition. While a file has type errors, hover and the type hints still show what did check, with `???` where an error left a type unknown; while it does not parse, they show what the compiler knew the last time it did, or, in a file that hasn't parsed since it was opened, what it knows of the definitions around the mistake, as the compiler reads past it
- inferred types in a lens above each top-level definition that does not write its type, and as inlay hints after locals, lambda parameters and pattern variables
- go to definition (from a name, or from a `` [`name`] `` link in a doc comment), find references (in every file of the package, for a file in one), both on a [dimension](../advanced/units-of-measure) where a type, a `unit` line or a derived dimension names it, and on a unit where a number is written with it, `km` in `2km`, or another unit's definition names it, the outline view, and **Go to Symbol in Workspace** (Ctrl+T), which finds a def, type, context or `module name =` block in any `.nyx` file under the workspace folders, open or not (outside `bin`, `obj`, `node_modules` and hidden folders), from the files as they are written, so a file with type errors still has its symbols
- completion of names, record fields, module members and import paths, each name with its doc comment and each module with the module's. The names the standard library keeps for itself, those that start with `__`, are never offered outside it. VS Code's own suggestions of words already in a file come only from the file being edited (`editor.wordBasedSuggestions` is `currentDocument` for Nanyx), so a standard-library file you opened from go to definition does not fill completion with its words. A word, for those suggestions and for double-clicking, is a name, a number, or a name with a `#` (a tag) or `@` before it
- quick fixes, such as importing an unknown name from the module that exports it. A name another module of the package exports offers **Import `Money` from `billing`**: the package's modules nearest the file first, then the public modules of the packages it depends on, then the standard library's. The fix adds the name to an import that selects names from that module, `import billing as (Invoice, Money)`; or adds the module to an `import ( ... )` block, in sorted order; or adds an import line. An import that would make a name the file uses ambiguous isn't offered, and the error says which name it would be
- [refactorings](#refactorings), in the light bulb menu and under **Refactor...**: on the name of a def or a lambda parameter that does not write its type, **Write the inferred type** writes it in, and on a `_` in an annotation it writes what the `_` was inferred to be; on a selected expression, **Introduce local** makes it a def of its own before the statement, and offers to replace the same expression later in the block too; on a selected expression or run of statements, **Introduce function** makes it a function of the locals it uses, a top-level def after the one it is in or a local one before the statement, and calls it; on a selected type, **Introduce type alias** makes it a `type` definition. Each starts renaming the name it makes up; on the name of a def or a use of it, **Inline** puts its value, or a function's body, in place of every use and removes the def, and **Inline this use** does it at one use; on the name of a call, **Convert to pipe** writes `f(x, y)` as `x \f(y)`, and on a pipe's name **Convert to call** writes it back, a chain of them as a whole
- **Organize Imports** (Shift+Alt+O, or **Source Action...**) removes the imports nothing uses and the names of a selection nothing uses, and writes the rest as one sorted import; it is also a quick fix of an unused import. To run it on save, add `"editor.codeActionsOnSave": { "source.organizeImports": "explicit" }` to the `[nanyx]` settings
- **Format Document**, which formats the file as `nanyx format` does, and **Format Selection**, which formats the definitions the selection touches
- **Rename Symbol** (F2) on a local, a parameter, a pattern variable, a def, a type, a dimension, a unit, an import's alias, a context's member, a record's field, a module or a `module name =` block: it renames every use in every file of the package (in the file alone, for a file in no package, where an exported name, a module or a block that exports names can't be renamed), renames a type's attached definitions' `Type.` part with it, a member where a `use` writes an instance of its context, a unit in every number written with it (`2km` to `2kilo`), refusing a name that is a unit already or that a number can't be written with, a field wherever a record of the same type writes it, a module in its `module` line and each import of it, with its file where the file is named for it, and a block in its `module` line, each import of it and each name written with it (`money.coins.penny`), and refuses, saying why, a new name that would change what any name refers to, or a field read from a record whose type isn't known
- **Expand Selection** and **Shrink Selection** (Shift+Alt+Right and Shift+Alt+Left), which grow the selection to the syntax around it, a name, then the call it is an argument of, the statement, the lambda, the def, and back
- an [info view](#info-view), a view in the secondary side bar that shows what the compiler knows at the cursor
- a [library browser](#library), which lists what the standard library exports, and a [search](#searching-the-library) of it by name and by type
- a run button, which runs the file as `nanyx run` does; a manifest has none
- in a `.nyxp` manifest, the problems with it as you type, each at the field it is about, and formatting

All of the settings below are under **Nanyx** in VS Code's settings, or can be written in `settings.json`. Changing any of them restarts the language server.

# Finding the compiler

The language server is built into the `nanyx` binary, which is not bundled with the extension. When you open a `.nyx` file, the extension looks for it in this order:

1. The `nanyx.path` setting, if it is set.
2. A `nanyx` on your `PATH`.
3. A compiler the extension downloaded earlier.

If none is found, it offers to download the newest compiler release for your platform into its own storage, so nothing else on the machine is touched. While it runs a downloaded compiler, it checks for a newer release at most once a day and offers to update. You can also run **Nanyx: Download the compiler** from the command palette at any time.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.path` | empty | The path to the `nanyx` executable itself (`nanyx` or `nanyx.exe`). Empty means look on `PATH`, then use a downloaded compiler. |
| `nanyx.compilerVersion` | empty | Pins the release to download and use, such as `0.5.2`. The extension then never looks for a newer one. Empty means the newest release. |
| `nanyx.downloadUrl` | empty | Where to download the compiler from, for a mirror or an unpublished release. `${version}`, `${rid}` (such as `win-x64`) and `${ext}` (`zip` or `tar.gz`) are filled in. The extension cannot list the releases at a custom URL, so it downloads `nanyx.compilerVersion`, or its own minimum version if that is empty. |

# How types are shown

Hovers, lenses, inlay hints and completions write types the way the compiler infers them. A type variable the compiler made up is named with a Greek letter, and a record's other fields are written with an ellipsis:

```nanyx
def identity = { x -> x } -- α -> α
def name = { p -> p.name } -- (name: α, …) -> α
def keep = { p -> (p.name, p) } -- (name: α, …ω) -> (α, (name: α, …ω))
```

The other fields get a name, as `…ω` does in `keep`, only where the type uses them more than once: here, to say that `keep` gives back a record with the same fields it was given. The other tags of a union are named from the end of the alphabet in the same way, as in `#some(α) | ω, (α -> β) -> #some(β) | ω`. A context variable the compiler made up is named from `κ` on, as in `def apply = { f, x -> f(x) }`, shown as `[κ] ([κ] α -> β), α -> β`; with `nanyx.asciiTypes` on, it is named from `c` on, `[c] ([c] a -> b), a -> b`. A type variable you wrote in an annotation keeps the name you gave it, and a type alias is shown by its name.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.asciiTypes` | off | Write types in ASCII only: type variables are `a`, `b`, `c` and the other fields of a record are `...`, as you would write them in source. Needs compiler 0.20.0 or later. |

`nanyx.greekTypeVariables` is a deprecated name for this setting. It still works, but use `nanyx.asciiTypes` instead.

# Semantic highlighting

The language server tells the editor what each name is, from what the compiler resolved it to, as semantic tokens of
the standard types, so your colour theme colours Nanyx's names as it colours other languages':

| Token type | For | Modifiers |
|---|---|---|
| `function` | a def or local def whose value is a function, at its declaration and each use; an attached definition's own name (`parse` in `Email.parse`); a context's member that is a function | `declaration`, `defaultLibrary` |
| `parameter` | a lambda's parameter | `declaration`, `mutable` for one that takes a cell (`mut cell`) |
| `variable` | a def or local def that is not a function, a pattern variable | `declaration`, `readonly`, `defaultLibrary`, and `mutable` instead of `readonly` for a cell made with `mut` |
| `property` | a record's field: in a record type, a record, a field read and a record pattern | `declaration` in a type |
| `enumMember` | a tag, `#some` | |
| `type` | a type's name; the type's part of an attached definition's name (`Email` in `Email.parse`) | `declaration`, `nominal`, `private` (see [Nominal types](#nominal-types)) |
| `interface` | a context's name | `declaration` |
| `namespace` | a module's name in its `module` line, an import's path and alias, and the alias before a name it qualifies | `declaration` |
| `operator` | an operator that calls a definition: an attached one, or a context's member | `defaultLibrary` |
| `keyword` | a workflow keyword, `try` or `return` | |
| `unresolved` | a name that names nothing in scope, such as a misspelt one: an unknown value, function, field, type, context or module, and the type an attached definition is attached to if there is none | |

`unresolved` is the extension's own token type, and `mutable`, `nominal` and `private` its own modifiers; the others
are standard. The extension maps `unresolved` to the scope `invalid.illegal`, which themes colour as an error, so an
unresolved name is red in most themes, besides the squiggle and the error in the Problems view. To colour it otherwise,
write a rule for `"unresolved:nanyx"` under `editor.semanticTokenColorCustomizations`. A file with type errors still
has tokens for what did resolve; while a file does not parse, it keeps those of its last text that did,
outside what you have edited since. To colour one kind of name your own way, write a rule for it under
`editor.semanticTokenColorCustomizations` in `settings.json`, such as `"variable.mutable:nanyx": { "underline": true }`.

# Nominal types

A [nominal type](../advanced/nominal-and-protected-types), one declared `protected` or `private`
(`type Email = protected string`), has a name written like any other type's. So that you can still tell it from an
alias of its shape, the language server marks each of its names, at its declaration, where a type names it, its
constructor, a pattern that takes it apart and the type's part of an attached definition's name (`Email` in
`def Email.parse`), as a semantic token with the `nominal` modifier. A private type's names also have the `private`
modifier. The extension shows them in italics, in any colour theme: it turns on semantic highlighting for Nanyx files
(`editor.semanticHighlighting.enabled`) and sets this default for `editor.semanticTokenColorCustomizations`:

```json
"editor.semanticTokenColorCustomizations": {
  "rules": {
    "*.nominal:nanyx": { "italic": true }
  }
}
```

To style them otherwise, such as giving private types a colour of their own, write your own rules in `settings.json`;
yours replace the default. `type.private:nanyx` selects the private ones. Needs compiler 0.28.0 or later.

# Type hints

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.topLevelTypeHints` | `codeLens` | Where to show the inferred type of a top-level definition that does not write its type: `codeLens` (in a lens above it), `inlayHint` (after its name, as for locals) or `off`. Hover still shows it when it is off. |
| `nanyx.inlayHints.localDefinitions` | on | Show the inferred type of each local `def` that does not write its type. |
| `nanyx.inlayHints.parameters` | on | Show the inferred type of each lambda parameter that does not write its type. |
| `nanyx.inlayHints.patternVariables` | on | Show the inferred type of each variable a pattern binds. |

Double-click an inlay hint to write its type into the file as an annotation: `def offset = 1` becomes `def offset: int = 1`, and `def apply = { f -> f(1) }` becomes `def apply = { f: ([c] int -> a) -> f(1) }`. What is written is source, whatever `nanyx.asciiTypes` says, so it type-checks to the same type. A pattern variable, or a name a destructuring `def (a, b) = ...` binds, can't be annotated, so its hint writes nothing; neither does a hint with `???` in it. Needs compiler 0.28.0 or later.

VS Code's own `editor.codeLens` and `editor.inlayHints.enabled` settings turn all lenses and inlay hints off. To change them, or any other editor setting, for Nanyx files only, put them under `"[nanyx]"`:

```json
{
  "[nanyx]": {
    "editor.inlayHints.enabled": "offUnlessPressed",
    "editor.formatOnSave": true
  }
}
```

# Formatting

**Format Document** formats a `.nyx` file with the [standard formatter](./formatter). **Format Selection** formats only the top-level definitions the selection touches, and leaves the rest of the file as it is. A file that does not parse is left as it is. To format every time you save, set `editor.formatOnSave` to `true`, as in the example above.

The lines that formatting would change are marked as you type, with a hint (three dots under the start of the first line) on each block of them, even while the file has type errors. Each has two quick fixes: **Format these lines** formats that block alone, and **Format the document** formats them all. A file that does not parse has no marks.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.formattingHints` | on | Mark the lines that `nanyx format` would change. |

# Refactorings

The refactorings change the code without changing what it does. Each one checks its result: where it can't keep the
program's meaning, it is shown in the menu disabled, with the reason, rather than offered. One that has to compile its
result to know is checked when you pick it, so moving the cursor stays fast; refused then, it changes nothing and shows
the reason as a message. What it writes is formatted
as `nanyx format` would format it. One that makes up a name, such as introducing a local, starts renaming the name
straight away, so you can type the one you want.

| Refactoring | Where | What it does |
|---|---|---|
| **Write the inferred type** | the name of a def or a lambda parameter that does not write its type | Writes the type the compiler inferred, as double-clicking its inlay hint does: `def add = { a, b -> a + b }` becomes `def add: [Add(a)] a, a -> a = { a, b -> a + b }`. |
| **Write the inferred type of this `_`** / **of every `_`** | a `_` in an annotation, or the name of a def whose annotation writes one | Replaces the `_` with what the compiler inferred it to be, or every `_` in the annotation: `def reduce: [Default(a)] list(a), _ -> a` becomes `def reduce: [Default(a)] list(a), (a, a -> a) -> a`. A type variable the annotation names keeps its name, and a new one takes the next free letter. |
| **Introduce local** | a selected expression | Makes it a def before the statement it is in, and uses the def in its place. A second offer replaces the same expression later in the block too, unless it calls something that requires a context or reads a cell, where each could give something different. |
| **Introduce function** | a selected expression, or a run of whole statements | Makes it a function, taking the locals it uses as parameters and giving back the ones the code after it uses, and calls it. One offer makes a top-level def after the one the selection is in, another a local function before the statement. |
| **Introduce type alias** | a selected type, in an annotation or a type definition | Makes it a `type` definition before the item it is in, inside the module block if the item is in one, and uses the alias in its place: `def keep: list(#some(int) \| #missing) -> int` becomes `type Extracted = list(#some(int) \| #missing)` and `def keep: Extracted -> int`. Its type variables are the alias's parameters, `type Extracted(a) = list(a)`. A second offer replaces every type written the same in the module, up to the names of their variables. The alias is exported where an exported def's signature uses it. It isn't offered for a type variable or a built-in type's name alone, a `_`, or a context in a list of contexts. |
| **Inline** | the name of a def, or a use of it | Puts the def's value, or a function's body, in place of every use and removes the def. **Inline this use** does it at one use. Arguments are bound by defs where putting them in would run them more or fewer times. |
| **Inline** a type alias | the name of a `type` alias, or a use of it | Puts the type it stands for in place of every use and removes the alias, its parameters replaced by each use's arguments: `Pair(int)`, for `type Pair(a) = (a, a)`, becomes `(int, int)`. **Inline this use** does it at one use. Refused for a `protected` or `private` type, which isn't an alias, and for an alias that names itself; an exported alias is inlined only a use at a time, since another module may use it. |
| **Convert to pipe** / **Convert to call** | the name of a call, or of a pipe | Writes `f(x, y)` as `x \f(y)`, and back. A chain converts as a whole: `list.filter(list.map(xs, f), g)` becomes `xs \map(f) \filter(g)`. |
| **Convert to shorthand lambda** / **Convert to lambda with parameters** | a lambda | Writes `{ x -> x * 2 }` as `{ * 2 }` and `{ a, b -> a + b }` as `{ + }`, and back, as it does `{ .name }` and `{ \f }`, naming the parameters `x` and `y` and starting a rename. Offered where the body is one operator applied to the parameters in order, each used once. Refused where a parameter writes its type, which a shorthand can't. |
| **Convert to if** / **Convert to match** | the `match` or `if` keyword | Writes `match ok \| true -> a \| false -> b` as `if ok then a else b`, and a match whose arms are each a literal or a tag, then `_`, as an `if ... else if ...` chain comparing with `==`: `match n \| 1 -> "one" \| _ -> "many"` becomes `if n == 1 then "one" else "many"`. Back, an `if` chain comparing one value with literals or tags becomes a match on it, and any other `if` a match on `true` and `false`. Refused where an arm binds a variable or has a guard, which an `if` has no place for, where an `if` condition's `is` tests bind names for its `then` branch, which a match on `true` and `false` has no place for, where the conditions compare different values, and where a chain would evaluate a value other than a name more or fewer times. |
| **Convert to interpolated string** / **Convert to concatenation** | a `++` of strings, or an interpolated string | Writes `"Hello, " ++ name ++ "! You are " ++ age \toString ++ "."` as `"Hello, {name}! You are {age}."`, and back. A `toString` call becomes a hole on its argument, as interpolation converts with `ToString`; back, a hole that isn't a string gets `\toString`. A string's escapes are kept. Refused for a raw (single-quoted) string, which can't interpolate, and a string of several lines. |
| **Implement** a context | the name of a `type` definition | Offers each context in scope that the type does not attach every member of, its member definitions being how a type gets a context. Picking one writes a stub for each missing member after the type's last attached definition: `def Point.compare: Point, Point -> #less \| #equal \| #greater = { a, b -> ??? }`. Fill in each `???`. A context the type attaches some of is offered first; one it attaches all of is shown disabled. A type with parameters is not offered. |
| **Make protected** / **Make private** | the name of a `type` definition whose type is structural | Writes `protected` or `private` before its shape, `type Email = protected string`, and adds `def Email.of: string -> Email`, which makes one from its shape and is exported if the type is, unless the module has an `Email.of` already. For an exported type, each place another file of the package gives a value of the shape where an `Email` is expected becomes a call to `Email.of`. Refused for a type whose shape is a type variable, for an exported type in a file in no package, whose users can't be found, and where another file reads the shape of a type made private. |
| **Import** a name | a name that is not in scope, from the **Refactor...** menu | Offers each import its error's quick fixes offer, as **Import `Money` from `billing`** or **Add `import nanyx/math`**. |
| **Organize Imports** | anywhere (Shift+Alt+O) | Removes the imports nothing uses, and the names of a selection nothing uses, and writes the rest as one sorted import. |
| **Rename Symbol** | a name (F2) | Renames it and every use of it, in every file of the package. |
| **Expand Selection** / **Shrink Selection** | anywhere (Shift+Alt+Right, Shift+Alt+Left) | Grows the selection to the syntax around it, and back. |

A refactoring is refused, among other cases:

- where it would move code that runs only sometimes, such as a branch of an `if` or a match arm, to where it always runs;
- where the code uses `try` or `return`, whose meaning depends on the block they are in;
- where a name would refer to something else afterwards, because a new name hides another or is hidden by one;
- where the result would not compile.

Inlining also refuses a recursive def and a def that is a cell or reads one. Introduce function refuses statements
with a `use`, an import or a type definition, which the statements after them may need. Organize Imports leaves the
imports alone where there are comments among them, which it would lose.

# Info view

The info view follows the cursor and shows what the compiler knows there. It lives in VS Code's secondary side bar (the
side bar on the opposite side from the Explorer, where Chat is), under a **Nanyx** tab, so it stays beside the code
while editors open and close. **Nanyx: Show info view** (Ctrl+Shift+Enter, or Cmd+Shift+Enter on a Mac, in a `.nyx`
editor) opens the secondary side bar if it is closed, shows the view and moves the focus to it; click back in the
editor, or press Ctrl+1, to carry on typing. You can drag the **Nanyx** tab to the primary side bar or to the panel,
and VS Code remembers where you put it. The extension needs VS Code 1.106 or later.

It is organised by scope, innermost first, with a rule between one scope and the next: the cursor line's messages, if
it has any, the expression or type you selected or the one at the cursor, each lambda around the cursor, each definition
around it (a local one as well as a top-level one), the `type`, `context`, `dimension` or `unit` declaration around it,
and the module. Each scope's heading names it by its code and links to it: the expression by its text,
`expression x + 1`, on one line and cut with `…` when it is long; a lambda by its parameters and line,
`lambda { message -> … } (line 4)`, or, for a shorthand lambda, as it is written, `lambda { * 2 } (line 4)`; a `for`
loop's body as the loop is written, `for words | word -> … (line 4)`; a definition by its name, `def greet`; a type's
declaration as it is declared, `context Iter(c)`; the module by its name.
Click the heading's arrow to open or close a scope. The module starts closed, and so does every scope after the third,
not counting the messages and the expression or type, since the side bar is narrow. A `memory` block or a `use` is part of the
scope it is in.

The view shows nothing twice. An item that repeats one a scope further down shows, the same text naming the same thing
at the same place, is left out, and the one further down is kept, where it has its context. With the cursor on a
definition's name, its signature shows once, in the definition's own scope, with its doc comment.

Types and contexts are named as the code at the cursor would name them, so a signature reads as you would write it:
inside `module Points`, which declares `Point`, it is `Point`, not `Points.Point`; elsewhere in the file it is `Point`
after `import Points` and `q.Point` after `import Points as q`. Where a type can be named more than one way, the view
uses the name with the fewest dots.

A signature too long for the view is laid out as the formatter lays out a definition's, at the view's width: the name
on its own line, then its type on the next, after `: `, broken where it is still too long: its parameters one per
line, its result's union one case per line, and its contexts on a line of their own
([Definitions](formatter#definitions)). It is laid out again when you resize the view. A line still too long for the
view, such as a long parameter's, wraps.

- **Messages**, only when the cursor's line has an error or warning: each one in full, as `nanyx run` prints it in a
  terminal, with more than the Problems panel shows. First its severity and code, such as `error NYX0201`, the code
  linking to its entry in the [errors reference](errors), and its headline, `Type mismatch`, linking to where it is
  reported. Under that, its notes, such as `= 'float' vs 'int'`, and the advice after `help:`; each other place that
  explains it, such as where a name was first defined, linked; and a button for each of its fixes, which makes the fix
  as its quick fix does. With no message on the line, the view starts at the expression, or the innermost scope.
- **The expression**, headed by its text, `expression x + 1`: its **Type**, the type of what you selected or clicked
  on, as hover writes it, as a definition has its signature. Select an expression to read its type: selecting `x + 1`
  in `def f = { x -> x + 1 }` shows `int`, with the spaces around the selection left out. A selection that is not a
  whole expression, such as `x +`, or that runs across two statements, shows nothing here. With nothing selected it is
  the innermost expression at the cursor, as hover finds it: on `x`, `int`. A name's doc comment shows under it.
- **The type**, in place of the expression when the cursor is on a type, or a type is selected. On a type's name, in an
  annotation, a signature or a type's definition, or on the name a declaration declares, the scope is headed as the
  type is declared, `type Point`, `context Iter` or `dimension Distance`, with its **Definition** as hover writes it,
  linked to where it is declared, and its doc comment; a built-in type such as `int` shows what hover says of it. A
  selected type, such as `list(Point)` in an annotation, is headed by its text, `type list(Point)`, with its **Type** as
  the compiler reads it; the selection is chosen as an expression's is, so `list(` shows nothing. A type variable, such
  as `a` in `def twice: [Num(a)] a -> a`, is headed `type variable a`, links to its first use, and lists the
  **Contexts** the signature requires of it, `Num(a)`.
- **A lambda**: its type, with the contexts it carries, such as `[Log] int -> ()`; its parameters, locals and pattern
  variables, each with its type, nearest first; and each `use` in it, with the context it gives.
- **A definition**: its signature, with its doc comment; its parameters and locals; its contexts, each with where it comes from (its signature, inference, or a
  `use` in it); and, in the innermost definition, its **Context flow**: each context its signature writes or it was
  inferred to need, each `use` in it, and each instance the compiler builds for a call in it, from the definitions
  attached to the type (`attached Point.show`), derived from the type's parts (`derived`), or its own (`built in`), with
  the calls each one serves under it. A `use` that serves no call is marked `unused`.
- **A type's declaration**, with the cursor anywhere in a `type`, `context`, `dimension` or `unit` declaration: its
  **Definition** as hover writes it, with its doc comment; its **Parameters**, each with the contexts it is bounded by,
  and a context's associated types, marked `associated type`; and the definitions **Attached** to it, such as
  `Point.norm: Point -> int`, each with its type and linked, whether it is declared beside the type or elsewhere in the
  module.
- **The module**: its doc comment, the top-level definitions and imports in scope, the file's own first, and the
  contexts the runtime supplies (`Console`, `Env` and `FileSystem`, from the host's prelude).

A link to the docs site, such as a message's code, opens in VS Code's built-in Simple Browser, in a tab beside the
editor, so you can read what an error means without leaving it. A link to a place in a file opens there, and any other
link opens as VS Code opens links.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.infoView.openDocsIn` | `editor` | Where a link to the docs site opens: `editor` in a Simple Browser tab, or `browser` in the system browser. If the Simple Browser extension is disabled, `editor` opens it in the system browser too. Hover's links, and a Problems entry's code, are opened by VS Code itself, so they always open in the system browser. |

A [custom pattern](../advanced/custom-patterns.md) is written as it was declared, `pattern string -> #Num(float) | _`,
marked `pattern`, among the names of the scope it is declared in, and its own scope is headed by its tags,
`pattern #Num`. Hover shows it the same way, on its declaration and on a tag a match arm uses, and the outline names it
by its tags.

While the view is visible and not pinned, it also highlights in the editor how contexts flow: with the cursor on a
`use`, or on a context in a definition's signature, the calls it serves are highlighted; with the cursor on a call,
the `use` or signature context it draws on is.

It can also join them with lines. A line runs down the left margin from the first highlighted line to the last, and
from it a line runs across to each highlight. A highlight with only spaces before it is met halfway up its line;
one further along a line, such as a context in a signature, is underlined, so the line does not strike through the
text before it.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.infoView.contextLines` | `full` | `full` (**Full context lines**) draws the line down and a line across to each highlight. `stubs` (**Context line stubs**) draws the line down with a short tick at each highlighted line. `highlight-only` (**Highlight only**) draws no lines. |

The lines are drawn in the `nanyx.contextLine` theme colour, which you can change under
`workbench.colorCustomizations`. VS Code has no API for drawing in the editor, so the extension draws them with
decorations and CSS, as extensions that draw indent guides do. They do not follow a wrapped or folded line.

Under each item that has one comes its [doc comment](../getting-started/basics#doc-comments), as hover shows it: a name's own, an
import's module's, a context's own, or, for a context a `use` gives, that of the definition the `use` names if it has one. A doc comment of
more than one paragraph shows its first; click it to show the rest, and again to cut it back. Its links go where the
names they name are declared.

The code it shows, in the headings and the items, is highlighted as the editor highlights it: with the extension's
grammar and the token colours of the current colour theme, with your `editor.tokenColorCustomizations` on top, in the
editor's font. It follows the theme when you change it. Messages are prose and are not highlighted.

Only the names in the code are links, and they look like the rest of the code until you hover over them. Click one to go
to it: a definition's, local's or parameter's name to where it is declared, a definition's name in its signature to the
definition, a context's name to the `use` or signature that gives it, a call in the context flow to the call, and the
name of a type or context with a definition, wherever the code writes it, to that definition (`Point` in
`#some(Point)`). A type with no name to follow, such as `int -> string`, has no link. **Pin**
keeps the view on the cursor's current position, and the selection there, while you move around and edit elsewhere;
**Unpin** follows the cursor again. The pinned position and selection move with your edits, as the text they are on
does: a line added above them, or text typed before them on their line, leaves the view on the same expression. Text
typed at the end of the selection goes after it. Deleting the text the position is on unpins the view; deleting the
text at either end of the selection leaves the view on the position alone. After an edit, the view catches up when the compiler has checked the new text, without the cursor moving. While
the file does not parse, the view shows what the compiler knew the last time it did. It needs a compiler
newer than 0.23.0.

# Library

The **Library** view, under the info view in the **Nanyx** tab, lists the standard library's modules, `nanyx/prelude`
first, each with the first line of its doc comment. Open a module to see what it exports, grouped into functions,
values, types and contexts, and then the definitions attached to each type under the type's name (`string.trim`
under `string`). Each export shows its signature; hover over it for its doc comment and the import it needs.

Click an export to open its declaration in the library's source, as go to definition does. The **+** button beside an
export of any module but the prelude, whose names need no import, adds that module's import to the active `.nyx`
file, where the quick fix for a missing import would put it. It adds nothing if the file imports the module already,
and nothing while the file does not parse. It needs a compiler newer than 0.28.0.

## Searching the library

**Nanyx: Search library by type**, or the search button at the top of the **Library** view, searches the standard
library as you type. Type a name (`trim`), a type written as a def's annotation is (`bool -> string`), or both, with
a colon between them (`trim: string -> string`). A type finds the functions whose types can be made equal to it: its
variables stand for any type, a function's inputs may come in any order, and a definition attached to a type takes
that type as its first input. `bool -> string` finds `bool.toString` first, then `ToString(a).toString`. Each match
shows its signature, the import it needs and the contexts it still needs at the types it matched, such as
`needs ToString(a)`; the best matches come first. Choose one to insert its name at the cursor and add its import to
the file. With no `.nyx` file open, it opens the match's declaration instead. `nanyx search` gives the same results
on the command line. It needs a compiler newer than 0.28.0.

# Running a file

The run button at the top right of a `.nyx` editor, or **Nanyx: Run the current file** from the command palette, saves the file and runs it with [`nanyx run`](./command-line#nanyx-run). The program runs in a terminal of its own in the panel, so it can read what you type, and Ctrl+C stops it. Running the file again uses the same terminal.

The program's working directory is the workspace folder the file is in, or the file's own folder when it is in none. The file runs as a script, even when it is part of a package. In [development mode](#working-on-the-compiler), it runs with `--timings`, so the terminal also shows how long each module took to check.

The command has no keyboard shortcut. To give it one, look for it in **Keyboard Shortcuts**.

# Commands

| Command | What it does |
|---------|--------------|
| **Nanyx: Run the current file** | Saves the file and runs it in a terminal. See [Running a file](#running-a-file). |
| **Nanyx: Show info view** | Shows and focuses the view that shows what the compiler knows at the cursor. See [Info view](#info-view). |
| **Nanyx: Search library by type** | Searches the standard library by name or type, and inserts the match you choose with its import. See [Searching the library](#searching-the-library). |
| **Nanyx: Download the compiler** | Looks for the newest compiler release straight away and downloads it. |
| **Nanyx: Restart the language server** | Stops the server and starts it again. |

# Working on the compiler

To try a change to the compiler in the editor, set `nanyx.path` to your build, such as `compiler/Nanyx.Compiler.Cli/bin/Debug/net10.0/nanyx.exe`, and turn on `nanyx.developmentMode`.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.developmentMode` | off | Runs the server from a copy of the build `nanyx.path` points at, so `dotnet build` can overwrite the build while the server runs, and restarts the server with a fresh copy whenever the build changes. Also starts the server with `--timings`, so the output channel shows how long each module took to check in each compile, not only how long the compile took, and runs a file with `nanyx run --timings`, so the run terminal shows the same tree. Needs `nanyx.path`. |

# Releases and what changed

The extension is released apart from the compiler: merging a change to the version in `extension/package.json` publishes it to the Visual Studio Marketplace and Open VSX, and tags it `extension/<version>` on GitHub. What changed in each release is listed in the extension's **Changelog** tab, in VS Code and in the Marketplace, and in its GitHub release's notes. Both are written when the extension is built, from the pull requests that changed `extension/` between one `extension/` tag and the next, one line each with a link, so nothing is kept by hand.

Most of what the editor shows, its errors, hovers, completions and formatting, comes from the `nanyx` compiler the extension runs, not from the extension, so each release's notes end with a link to the [compiler's changelog](../overview/changelog).

# Troubleshooting

The **Nanyx** output channel logs how the extension found or downloaded the compiler, the server's start-up, how long each compile of a file took, and any errors.

- **"nanyx.path is set to ..., but no file exists there"**: fix the path, or clear the setting to go back to `PATH` and the downloaded compiler.
- **"Could not reach github.com to download the Nanyx compiler"**: you are offline, or a proxy is in the way. Connect and run **Nanyx: Download the compiler**, or install the compiler yourself.
- **"No compiler release was found at ..."**: there is no release for that version. Set `nanyx.compilerVersion` to one that exists, `nanyx.downloadUrl` to where it is, or `nanyx.path` to a compiler you have.
- **No errors appear**: check the output channel, then run **Nanyx: Restart the language server**.
- **A setting has no effect**: some settings need a newer compiler than the one you have. Update it with **Nanyx: Download the compiler**.
