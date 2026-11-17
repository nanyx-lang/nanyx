---
title: "The parser"
description: "The parser corpus, the differential test and the lexer"
order: 12
---

The parser (`compiler/Nanyx.Parser`) is written with [FParsec](https://www.quanttec.com/fparsec/) combinators. It is to
move to a lexer and a hand-written recursive-descent parser, in the stages [The parser](../work-in-progress/parser) in
Work in progress describes. This page says what stages 0 and 1 built:

- **The corpus** (`Corpus.fs` in `compiler/Nanyx.Parser.Corpus`, a library only the test projects reference): every
  `.nyx` file in the repository, every ` ```nanyx ` block in the docs, and every triple-quoted string in the test
  projects' sources. The formatter's corpus tests read it too.
- **The differential test** (`Differential.fs`, with `CorpusTests.fs` in the parser's tests). It parses each piece with
  two parsers, writes each result out with every span and without node ids (`SyntaxDump.fs`, which walks the tree by
  reflection), and reports the pieces whose trees, comments, blank lines or errors differ, each at its first
  differing line. Until there is a second parser it runs today's parser against itself. Every later stage is checked
  by it.
- **The lexer** (`Lexer.fs`): text in, tokens out, each with its offsets and its leading and trailing trivia, lossless
  whatever the text. A string with holes is a `StringStart`, the hole's tokens, a `StringMiddle` between holes and a
  `StringEnd`. Operators come from `Operators.binaryOperators`, and keywords from `Keywords.reserved`. The FParsec
  parser doesn't use it yet. `CorpusTests.fs` checks that every piece of the corpus is written back exactly from its
  tokens, and that every piece the parser reads lexes with no error and has exactly the comments the parser finds.
