---
title: "The parser"
description: "The lexer, the layout pass and the recursive-descent parser, how they report mistakes and read past them, and the corpus they are tested on"
order: 12
---

The parser (`compiler/Nanyx.Parser`) reads a file in three steps: a lexer turns the text into tokens, a layout pass
makes the block structure that indentation gives them explicit, and a hand-written recursive-descent parser reads the
tokens into the syntax tree (`Ast.fs`). [The parser](../work-in-progress/parser) in Work in progress says what is still
open.

- **The lexer** (`Lexer.fs`): text in, tokens out, each with its offsets and its leading and trailing trivia, lossless
  whatever the text. A string with holes is a `StringStart`, the hole's tokens, a `StringMiddle` between holes and a
  `StringEnd`. Operators come from `Operators.binaryOperators`, and keywords from `Keywords.reserved`, the one list of
  reserved words, so a keyword is never read as part of a longer name.
- **The layout pass** (`Layout.fs`): the lexer's tokens in, the same tokens out with `Indent`, `Newline` and `Dedent`
  among them, each at the offset of the code token after it, by the rules below. The parser never measures
  indentation itself.
- **The recursive-descent parser** (`DescentCursor.fs` and a file per part of the grammar: `DescentTypes.fs`,
  `DescentLiterals.fs`, `DescentPatterns.fs`, `DescentExpressions.fs`, `DescentBlocks.fs`, `DescentControl.fs` and
  `DescentTopLevel.fs`). `DescentCursor` has the cursor, which reads the layout pass's tokens and goes back for an
  alternative (`NoMatch`, which `Cursor.Attempt` catches), and the side state kept beside the tree (`ParseState`): the
  names nodes declare, where documentable constructs start, `module name =` blocks, comments from the lexer's trivia,
  and string texts. The files read, in order: types, with a field's default and a `where` constraint read as
  expressions; literals, with a `+` or `-` written straight before a number as its sign and `1.` as a float, though the
  lexer reads both apart; patterns, with a guard's lambda and a custom pattern's arguments read as expressions;
  expressions, with the binary operators and ranges read by precedence climbing over `binaryOperators`, either going
  on after an operator at the end of a line (`Crossing`) or, for a statement, not (`Inline`); lambdas, a def's value
  written as an indented block, a match's arms and the statements of a block, with destructuring defs desugared by
  `Destructuring.fs`; what starts with a keyword (`match`, `if`, `for`, `while`, `do`, `use ... in`, `return`, `try`,
  `yield`, `name!`, `mut x` as an argument and `type T`); and the top level, with its directives blanked
  (`Directives.fs`). The files before `DescentControl.fs` read lambdas, expressions, arms and what starts with a keyword
  through forward references (`DescentCursor.Forward`), which `DescentTopLevel.fill` fills before anything is read.
  `Api.runModuleParser` reads a whole file (`DescentTopLevel.tryReadModuleWithState`), and `parseTypeSignature`, which
  `nanyx search` reads a query's type with, reads a type alone (`DescentTypes.readTypeSignature`).
- **Its errors.** A mistake the parser has a message of its own for is reported with that message where it is
  (`Reported`, raised by `Cursor.Report`): an operator that isn't spaced, `&&`, a prefix `!`, two operators that can't
  be written side by side, a `$` before a type's name, an escape that isn't one, a `_` not between two digits, and the
  rest. The lexer's own mistakes (a string or a block comment never closed) are reported as the lexer words them.
  Anything else is reported where the furthest reading failed, as what was expected there and what was found
  (`Cursor.Unexpected`): each reading notes what it expected when it fails (`Cursor.Fail`, and `Cursor.Expect` for a
  token), and those at the furthest token are listed.
- **Reading past mistakes.** The compiler reads a file with `Api.parseModuleFileWithDocsRecovering`
  (`DescentTopLevel.readModuleRecovering`), which goes on after a mistake: the top-level item it is in is left out,
  the error is noted, and reading goes on at the first line after the item's start, from the error's line on, that
  starts at the first column. An item read before the error that runs on into its line, as `def d = c +` does into a
  `def` under it, is left out with it. Every error is reported, the lexer's too, except a parse error after the first
  of the lexer's, which has taken the rest of its line or the file. The items that read are typed, so the editor has
  hover, completion and the outline around the mistake, but only the syntax errors are reported (`compileChecked`), as
  the errors that come of the items left out aren't the user's to fix, and the module can't be run or imported. The
  language server reads such a compile only when no earlier text of the file parsed (`OpenDocument.LastGood`): while
  the user is typing, the item being typed is the one left out, and the last text that parsed still has it. The
  formatter reads with `parseModuleWithTrivia`, which stops at the first mistake, and leaves a file with one alone.
- **The corpus** (`Corpus.fs` in `compiler/Nanyx.Parser.Corpus`, a library only the test projects reference): every
  `.nyx` file in the repository, every ` ```nanyx ` block in the docs, and every triple-quoted string in the test
  projects' sources. The formatter's corpus tests read it too. Listing it never enters `bin`, `obj`, `node_modules`,
  `.git` or `.claude`, so a checkout's worktrees cost nothing. Each test project parses the corpus once, in parallel,
  in a value its tests share, and the formatter's tests format and re-parse each piece once the same way.
  `CorpusTests.fs` checks that every piece is written back exactly from its tokens, that every piece that parses lexes
  with no error, and that the layout pass gives every piece that parses the block structure its tree has
  (`BlockStructure.fs`: the items of each block, module block, match, `except`, multi-branch `if` and union type, each
  as the offset it starts at, keeping only the items that start their line and the groups of two items or more). A
  few pieces differ on purpose, each listed with why in the test.
- **The differential test** (`Differential.fs`, with `CorpusTests.fs` in the parser's tests). It parses each piece with
  two parsers, in parallel, writes each result out with every span and without node ids (`SyntaxDump.fs`, which walks
  the tree by reflection), and reports the pieces whose trees, comments, blank lines or errors differ, each at its
  first differing line. It runs the parser against itself, so that a parse that depends on anything but its text
  shows up, and is there for a change to the parser to be checked against the parser before it.

# The layout rules

Columns are counted from the start of the line, and a line is the first token on it and the tokens after it. An
_opener_ is a token that can open a block, as the last token on its line: `{`, `=` (a def's, a type's or a module
block's), `->` (a lambda's or an arm's), `then`, `else`, `do`, `in`, or `match` with its scrutinee on the lines after
it. A keyword written straight after `#` or `.` is a name, as the tag `#match` is, and opens nothing. A block is a run of
items at one column; the pass keeps a stack of them, with the brackets and braces open inside them.

1. **The top level** is a block at the column of the first token.
2. **An opener opens a block.** After an opener, a line indented further than the innermost block opens a block at
   its column, with an `Indent` before it. After a `{` the block may be at any column, since the `}` closes it.
3. **A match's arms are a block.** A line that starts with `|` opens a block of arms at its column when it is
   indented further than the innermost block, or at the column of a block that isn't arms, so that arms may line up
   with their `match`. In `[` and `]` it doesn't: a table's rows start with `|`.
4. **A line indented less than a block closes it,** with a `Dedent`, and then the next block out, while the line is
   indented less than each. So does a line at a block of arms' column that doesn't start with `|`. So a match nested
   in an arm ends at the next arm indented back to the outer one
   ([#249](https://github.com/nanyx-lang/nanyx/issues/249)).
5. **A line at the innermost block's column starts its next item,** with a `Newline` before it, unless it starts with
   `then` or `else`, or the line before ends with `in`: those continue the item, so an `if`'s branches may line up
   with it, and the body after `use ... in` may be at the `use`'s column
   ([#673](https://github.com/nanyx-lang/nanyx/issues/673)). Any other line, indented further than the block, continues
   the item it is in, with no layout token: a pipeline's next step, and an `and` or `or` on a line of its own
   ([#617](https://github.com/nanyx-lang/nanyx/issues/617), [#628](https://github.com/nanyx-lang/nanyx/issues/628)).
6. **Brackets have no layout in them.** In `(` and `)`, `[` and `]`, and a string's holes, every line continues the
   item, and indentation closes no block outside them. A block opened inside them, after an opener or by arms, closes
   when they do, and a `}` closes the blocks opened inside its `{`, each with a `Dedent`. A line that starts with a
   closing bracket or brace is part of what it closes, so a list's `]` on its own line is no item
   ([#248](https://github.com/nanyx-lang/nanyx/issues/248)).
7. **The end of the text** closes every block but the top level's.
