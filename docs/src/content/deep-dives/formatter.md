---
title: "Formatter"
description: "How nanyx format is built"
order: 13
---

The formatter's style, settings and command are described for users on [Formatter](../standard-tools/formatter), and
its editor features on [VS Code extension](../standard-tools/vs-code-extension#formatting). This page says how it is
built. Work is tracked under the [`formatting`](https://github.com/nanyx-lang/nanyx/labels/formatting) label.

There is one official formatter, `nanyx format`, so that there is one layout for Nanyx code and nobody argues about
it. This page is about what the user's page leaves out: how the formatter keeps comments the parser would throw away,
how it decides where lines break, and how it guarantees it never changes what a program means.

| Area | Where |
|------|-------|
| Comments and blank lines | Kept by the parser beside the syntax tree (`SourceTrivia.fs`), so the formatter can put them back |
| Printing | `compiler/Nanyx.Formatter`: a syntax tree in, Nanyx source out |
| CLI | `nanyx format` (`FormatCommand`) |
| Editor | `FormattingHandler` and `RangeFormatting` in the language server |
| Info view | `SignatureLayout.layOut`: a signature written for a reader (`name: type`), laid out as a def's at the view's width; only one that doesn't fit on a line as it is ([#2406](https://github.com/nanyx-lang/nanyx/issues/2406)) |

The formatter only moves whitespace and comments, adds or removes brackets and blank lines, and sorts imports. Style
diagnostics (naming, unused code, preferring one construct to another) belong to the
[official linter](../standard-tools/official-linter).

# Keeping comments

The parser records every comment and every run of blank lines it skips, with its span, in a side list returned with
the syntax tree. The tree itself does not change, so the type checker and code generator are untouched. The formatter
attaches each comment to a node:

- A comment on the same line after code is a *trailing* comment of the last node that ends on that line before it,
  and stays on that line. A construct written on one line that the layout breaks keeps its comment on its first line:
  the comment is put before it, and ends the first line the construct is laid out on (`CommentPlacement.Sequence`).
  A comment after a lambda's `{` or `->`, before its body, stays there (`LambdaPrinter.lambdaDoc`), and so does one
  after a match arm's `->`, before its result: at the end of the `->`'s line, or on a line of its own at the result's
  indent, with the result on the line after it (`BranchPrinter.armBodyAfter`).
  A comment after the `=` of a union written over lines stays at the end of that line, and those among its cases stay
  beside or above the case they were written with (`Formatter.unionPerLine`).
- A comment on a line of its own before an `if`'s `else` stays on its own line before the `else`, at its indent, and
  one after the `else` on its line stays there (`BranchPrinter.commentsAroundElse`).
- A comment on a line of its own is a *leading* comment of the next node that starts after it, and is printed on its
  own line, at that node's indent, before it.
- A comment with no following node in its block (the end of a file, a match or a lambda body) is printed at the end
  of that block. One before the `}` of a lambda whose body is on the `{`'s line breaks the lambda over lines, so that it
  has a line inside the lambda to go on (`LambdaPrinter.closingComments`).
- A `---` block comment is always on lines of its own. Its opening line of dashes goes at the indent of the code
  around it; the rest of its lines, the closing one included, are printed exactly as written.
- A `--|` doc comment is a leading comment too. Its text and line breaks are kept, without rewrapping, and each
  of its continuation lines is set one indent step past its first line, keeping their indents relative to each other.

# Printing

The formatter is a document printer in the style of Wadler's "prettier printer" (as in Prettier, `ocamlformat` and
Elm's formatter): each construct is turned into a document of text, possible line breaks, nesting and groups, and the
printer lays out each group on one line if it fits in the line width and breaks it otherwise. The rules for each
construct are written once, as documents, and the printer decides where to break.

- Parentheses are not kept by the parser (`tupleOrParenExpr` returns the inner expression), so they are printed where
  precedence needs them, and nowhere else. A record of plain named fields that is a type application's only argument
  or a tag's payload shares its brackets: `list(price: int)`, not `list((price: int))`
  ([#1628](https://github.com/nanyx-lang/nanyx/issues/1628)). A construct that takes everything after it, such as an
  `if`, a `match` or a `with` (`Formatter.isOpenEnded`), is bracketed unless nothing follows it (`Position.Tail`). A
  workflow keyword applied to a value, `try x`, `yield x` or `return x`, that is an item in brackets (an argument, a
  field, an element or an entry) is not, since the parser ends it at the item's `,` or line break
  (`Position.endsWithItsItem`, [#2411](https://github.com/nanyx-lang/nanyx/issues/2411)).
- A tuple is bracketed but for a def's value, after the `=` of a `def`, a destructuring def or a `mut`, where the
  parser reads it the same without: `def point = 3, 4`, `def x, y = 1, 2` (`Formatter.defValueDoc`). It is bracketed
  there too when it is broken over lines, or when an item is an `if`, a `match` or a block, which would take in the
  items after it. Everywhere else it keeps its brackets, as the parser needs them: a lambda's body, a match arm's
  result, a block's last expression, `return`, `:=`, an argument and an item of a list
  ([#2653](https://github.com/nanyx-lang/nanyx/issues/2653)).
- Literals are stored by value (`IntLit of int`, a decoded `StringLit`), so they are printed from their source text,
  through their spans, and `0xFF`, `1_000` and escapes survive. The text between the holes of an interpolated string
  has no span of its own: `SourceText.WrittenStringText` finds it in the source after the opening quote or the hole before it,
  and keeps it if `StringEscapes.decode` gives back the parsed text.
- Identifiers, operators and keywords are printed as written; the formatter never renames.
- A table literal is parsed as the list of records it stands for, so the formatter tells it from one by its source
  starting `[|` (`CallPrinter.tableDoc`). It prints each cell flat, pads each column to its widest cell, and indents the
  rows one column in from the line the table starts on. With a comment among the rows, it prints the list instead, one
  record per line, as the rows give a comment no line of its own to go on. A table stays where it was written: moving
  one from a def's line to the next would make the def's value a block.
- Whether a construct spans more than one line, which decides the blank lines around it, depends on its layout, which
  depends on where it starts, so the renderer decides it (`Doc.Stack`): a construct is multi-line if it always breaks,
  or if it does not fit flat in the width left at its indent.
- A pipeline's stages are indented once from the line it starts on because the parser needs a stage that continues a
  statement to be indented more than the statement's line. A chain of operators stays on one line in a statement
  because the parser takes a line break after an operator but not before one, and not at all there. A lambda's body
  on its own line is read as a statement, which takes every operator but `..`, so a range stays on the `->`'s line
  rather than being bracketed ([#312](https://github.com/nanyx-lang/nanyx/issues/312),
  [#960](https://github.com/nanyx-lang/nanyx/issues/960)).
- A def's signature is printed by `TypePrinter.brokenSignature`, which writes on one line what `signature` writes, as
  nested groups: the contexts and the rest of the type, then the parameters and `-> result`, then a result union's
  cases. Each breaks only where it doesn't fit once the one around it has broken, and the lines after the first are
  nested by the width of what they line up under (`: `, `-> `, `[`), not by an indent step
  ([#2270](https://github.com/nanyx-lang/nanyx/issues/2270)). A union's cases on lines of their own after `-> |` are
  a block of arms to the layout pass, whose `Dedent` the type parser takes, so the `=` after them still parses.

# Never changing meaning

`nanyx format` refuses a file that does not parse, and says where the parse error is.

After formatting, it parses its own output and compares the tree with the original (equality on the syntax tree
compares `Kind` only, ignoring spans), with the original's imports sorted the same way first. If they differ, or a
comment was dropped, it leaves the file alone and reports an internal error with the file's name. This check runs on
every file, always: it costs one extra parse, and a formatter that silently changes a program is worse than none.

Formatting is idempotent: formatting formatted code changes nothing. Both properties are tested over every `.nyx`
file in the repository and every Nanyx code block in the docs.

# Sorting imports

The order is `compiler/Nanyx.Formatter/ImportOrder.fs`; `ImportPrinter.fs` applies it. Within each group the sources
are compared as written, ignoring case, with ties broken by case, and the alias doesn't affect the order. Duplicate
imports are left alone: removing them is a linter's job. Imports inside a block (`ImportStatement`) have their names
sorted within one `import (...)`, but separate import statements in a row in a block are not merged or reordered.

The safety check sorts each side's imports before comparing the trees (`Syntax.normalize`), so a sort that dropped or
duplicated an import is still caught. It compares the comments at the end of import lines as a sorted list, since they
move with their imports, and every other comment in order (`FormatCommand.commentsOf`, which the corpus tests use too).

Reordering imports is safe because an import only brings names into scope, and a name exported by two imports is an
error whatever their order (#155). If imports ever gain effects, this rule changes with them.

# Configuration

`nanyx format` finds the nearest `.nyxp` in the file's directory or above it, reads it as data (`DataValue.read`), and
looks only at its `formatter` field (`Settings.read`); the compiler checks the rest of the manifest. The language
server uses the same settings for the document it formats.

# The command

`--stdin` formats the input as a file named `<stdin>` in the current directory, with that directory's settings, and
messages name it so ([#858](https://github.com/nanyx-lang/nanyx/issues/858)). It is a flag rather than
`nanyx format -` because the CLI's argument parser reads `-` as an option.

# The editor

The language server answers `textDocument/formatting` with one edit replacing the document, or no edits if it does
not parse. It answers `textDocument/rangeFormatting` by formatting the top-level items the range touches
([#857](https://github.com/nanyx-lang/nanyx/issues/857)): the whole document is formatted, with the same check, and
the lines from the first such item's to the last's are replaced with the same items' lines in the formatted text
(`RangeFormatting.formatRange`). The comments and blank lines around them are left as they are. When formatting
changes how many top-level items there are, as joining imports does, the whole document is replaced.

The lines formatting would change ([#705](https://github.com/nanyx-lang/nanyx/issues/705)) are one Hint diagnostic,
with the code `format`, per block of whole lines that formatting changes (`FormattingHints`, from the blocks
`LineDiff` finds by Myers' algorithm). It runs the formatter itself rather than through a linter, so it works on a
module with type errors; the hints and **Format Document** both read `FormattingHandler.FormattedText`, so they never
disagree. `nanyx lsp --no-format-hints` turns them off.

# The repository's Nanyx

`stdlib/` and `packages/` are checked by `nanyx format --check` in CI
([#223](https://github.com/nanyx-lang/nanyx/issues/223)). The examples, the docs site's home page's examples
(`docs/src/home`), and the docs' code blocks fenced ` ```nanyx `, are checked by a test that CI runs (`CorpusTests`, "The examples and the docs' code blocks are
formatted"), which names each one that isn't formatted and shows what it should be. A test rather than a CLI step,
because the command can't read a Markdown file. Every example in `examples/` parses, and a test fails naming each one
that doesn't, with its parse error ("Every example parses"); `RunExampleTests` runs each example and checks what it
prints, or lists it as not run, with why.
A block fenced ` ```nanyx fragment `, ` ```nanyx skip=#<issue> ` or ` ```nanyx unformatted ` (code shown before
formatting, as on the Formatter page) is not checked, and nor is one with an `-- error` comment, whose code is wrong on
purpose (the formatter would write NYX0032's `&` as `,`) and laid out to put the comment on the line it is about. The
style examples on the Formatter page are tested (`StyleExampleTests`).
