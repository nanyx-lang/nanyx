---
title: "The parser"
description: "The parser corpus, the differential test, the lexer, the layout pass and the recursive-descent parser so far"
order: 12
---

The parser (`compiler/Nanyx.Parser`) is written with [FParsec](https://www.quanttec.com/fparsec/) combinators. It is to
move to a lexer and a hand-written recursive-descent parser, in the stages [The parser](../work-in-progress/parser) in
Work in progress describes. This page says what stages 0 to 2 built, and what stage 3 has built so far:

- **The corpus** (`Corpus.fs` in `compiler/Nanyx.Parser.Corpus`, a library only the test projects reference): every
  `.nyx` file in the repository, every ` ```nanyx ` block in the docs, and every triple-quoted string in the test
  projects' sources. The formatter's corpus tests read it too. Listing it never enters `bin`, `obj`, `node_modules`,
  `.git` or `.claude`, so a checkout's worktrees cost nothing. Each test project parses the corpus once, in parallel,
  in a value its tests share, and the formatter's tests format and re-parse each piece once the same way.
- **The differential test** (`Differential.fs`, with `CorpusTests.fs` in the parser's tests). It parses each piece with
  two parsers, in parallel, writes each result out with every span and without node ids (`SyntaxDump.fs`, which walks the tree by
  reflection), and reports the pieces whose trees, comments, blank lines or errors differ, each at its first
  differing line. Until there is a second parser it runs today's parser against itself. Every later stage is checked
  by it.
- **The lexer** (`Lexer.fs`): text in, tokens out, each with its offsets and its leading and trailing trivia, lossless
  whatever the text. A string with holes is a `StringStart`, the hole's tokens, a `StringMiddle` between holes and a
  `StringEnd`. Operators come from `Operators.binaryOperators`, and keywords from `Keywords.reserved`. The FParsec
  parser doesn't use it yet. `CorpusTests.fs` checks that every piece of the corpus is written back exactly from its
  tokens, and that every piece the parser reads lexes with no error and has exactly the comments the parser finds.
- **The layout pass** (`Layout.fs`): the lexer's tokens in, the same tokens out with `Indent`, `Newline` and `Dedent`
  among them, each at the offset of the code token after it, by the rules below. The FParsec parser doesn't use it
  yet. `CorpusTests.fs` checks that it gives every piece of the corpus that parses the block structure the parser
  gives it (`BlockStructure.fs` in the corpus project): from the tree, the items of each block, module block, match,
  `except`, multi-branch `if` and union type, and from the layout tokens, the items of each block they make, each as
  the offset it starts at, keeping only the items that start their line and the groups of two items or more. A few
  pieces differ on purpose, each listed with why in the test: where the two read text differently is listed in
  [The parser](../work-in-progress/parser) in Work in progress.
- **The recursive-descent parser** (`DescentCursor.fs` and a file per part of the grammar, so far `DescentTypes.fs`,
  `DescentLiterals.fs`, `DescentPatterns.fs`, `DescentExpressions.fs` and `DescentBlocks.fs`):
  the FParsec parser's grammar read from the layout pass's tokens. `DescentCursor` has the cursor, which reads the
  tokens and goes back for an alternative (`NoMatch`, which `Cursor.Attempt` catches, as FParsec's `attempt` and `<|>`
  do), and the names and documentable starts the FParsec parser keeps in `ParseState`. At anything it doesn't read yet,
  and at any mistake, it gives up (`GiveUp`) and the FParsec parser reads the text instead, so that errors are still
  reported as they were. It reads types, and gives up at one that holds an expression (a field's default or attribute,
  a `where` constraint). `parseTypeSignature`, which `nanyx search` reads a query's type with, uses it. `CorpusTests.fs`
  checks it against the FParsec parser on every def's annotation and type definition's body in the corpus
  (`DescentCheck.fs` in the corpus project): each must read to the same tree with the same spans, or give up at an
  expression. It reads literals as the FParsec parser does, with a `+` or `-` written straight before a number as its
  sign and `1.` as a float, though the lexer reads both apart. It reads patterns, trying the alternatives in the
  FParsec parser's order, and gives up at one that holds an expression other than a literal (a guard that is a lambda, a
  custom pattern's arguments). `CorpusTests.fs` checks it on every pattern of every match arm in the corpus, and
  `DescentPatternTests.fs` on the places where reading one takes care. It reads expressions: literals, strings with
  holes, names (a keyword that doesn't start a construct of its own is a name, as `identifierNoWs` reads it), calls,
  tags, member access, `with`, tuples, records, lists and maps, the prefix `not` and `-`, pipes, `:=`, and the binary
  operators and ranges by precedence climbing over `binaryOperators`, giving up where two operators can't be written
  side by side. It reads as either of the FParsec parser's operator tables does (`Lines`): `Crossing` goes on after an
  operator at the end of a line, and `Inline`, for a statement, doesn't. It gives up at what starts with a keyword
  (`if`, `match`, `for`, `use`, ...). `DescentBlocks.fs` reads lambdas, which `DescentExpressions` reads through a
  forward reference (`lambdaRef`, filled by `DescentBlocks.fill`), a def's value written as an indented block, and the
  statements of a block: `def`, `export def`, `rec`, `mut` and expressions, several on a line after `;`. A lambda's
  statements are any number at any column until its `}`, as the FParsec parser reads them, and a block's run from the
  `Indent` that opens it to the `Dedent` that closes it, with a line indented further that its statement before didn't
  take as a statement of its own. It gives up at a lambda of match arms, one that names its context's instance or
  writes its result type, a destructuring def, and a `use`, `import`, type or attribute statement. `CorpusTests.fs`
  checks it on every def's value and every statement in the corpus, and `DescentExpressionTests.fs` and
  `DescentBlockTests.fs` on the places where reading one takes care. Nothing reads with the pattern, expression and
  block parsers yet but those tests.

# The layout rules

Columns are counted from the start of the line, and a line is the first token on it and the tokens after it. An
_opener_ is a token that can open a block, as the last token on its line: `{`, `=` (a def's, a type's or a module
block's), `->` (a lambda's or an arm's), `then`, `else`, `do`, `in`, or `match` with its scrutinee on the lines after
it. A block is a run of
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
