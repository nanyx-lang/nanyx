---
title: "The parser"
description: "Moving the parser from FParsec to a lexer and a hand-written recursive-descent parser, in stages"
order: 34
---

This page is how the parser should work, and how to get there from today's parser without a rewrite all at once.
The maintainer decided in [#846](https://github.com/nanyx-lang/nanyx/issues/846), which tracks the work, to go all the
way, through stage 6, in the stages below. Error recovery (stage 5) is what the language server and the
[refactorings](./refactorings) need most. Error messages that change at stage 4 are accepted after review, following
the [house style](../foundations/principles#friendly-errors).

# Today

The parser (`compiler/Nanyx.Parser`) is written with [FParsec](https://www.quanttec.com/fparsec/) combinators. It
reads characters directly. It backtracks with `attempt`, and layout (where a block, a match or a pipeline ends) is
decided by the individual parsers, each measuring indentation itself. Most of the parser bugs fixed so far come from
that approach rather than from the grammar:

- **Keywords leak into names.** A keyword parser can match the start of a longer name (`typeName`, `trueValue`). The
  reserved words are one list, `Keywords.reserved`, but the parsers that match a keyword each write it out again.
- **Backtracking hides errors.** An `attempt` that rewinds past the real problem reports it somewhere else, and
  FParsec's "Expecting: any char in ' ', for loop, function call, ..." messages are the same effect. Because choice
  order and `attempt` placement are behaviour, [Working in this repository](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md)
  has a rule just to protect them.
- **Layout is decided in many places.** A nested match ending at an arm indented back
  ([#249](https://github.com/nanyx-lang/nanyx/issues/249)), a list's `]` on its own line
  ([#248](https://github.com/nanyx-lang/nanyx/issues/248)), `and`/`or` after a call or in a match arm
  ([#617](https://github.com/nanyx-lang/nanyx/issues/617), [#628](https://github.com/nanyx-lang/nanyx/issues/628)) and
  the block after `use ... in` ([#673](https://github.com/nanyx-lang/nanyx/issues/673)) were each a layout case one
  parser handled and another didn't.
- **Comments are kept on the side.** The parser records comment positions in its state (`Comments` in
  `ParseState.fs`); the formatter and doc comments attach them by position afterwards.
- **The first error ends the parse.** The parser has no error recovery, so the editor gets no outline, no hover and
  no completion below a half-typed line. Stage 5 settled this for the new parser.

# Where to end up

A **lexer** that turns the text into tokens, each carrying the comments and whitespace around it; a **layout pass**
that turns indentation into explicit tokens (the offside rule, as in F# and Haskell); and a **hand-written
recursive-descent parser** over those tokens. This is how Roslyn, rust-analyzer, the TypeScript compiler and Go's
parser are built, because they serve editors as well as compilers.

- A name is one token, so a keyword can never match part of one, and there is one list of reserved words.
- Layout is decided once, by one set of rules, measured from the start of the line as every layout rule is.
- The parser looks ahead at tokens instead of backtracking over characters, so an error is reported where it
  happens, with the tokens it expected.
- It can recover: skip to the next statement or definition, record the error, and carry on, so the rest of the file
  still has a tree.
- Comments are part of the token stream, so the formatter and doc comments get them without a side list.

A parser generator (FsLexYacc, ANTLR) is not proposed: it would make error messages and recovery worse, and
indentation-sensitive layout fits it badly.

# In stages

The parser changes more often than almost any other part of the compiler, so a long-lived rewrite branch would never
merge. Each stage lands on its own, keeps every test passing, and is useful by itself. The syntax tree (`Ast.fs`) does
not change in stages 1 to 4, so the type checker, the backend, the formatter and the language server need no change
until the parser underneath them is replaced.

## Stages 0 to 2

Stages 0 to 2 are done: the corpus, the differential test, the lexer and the layout pass, described in
[The parser](../compiler-internals/parser) in the Compiler internals section, with the layout rules and where the
layout pass reads text differently from the FParsec parser.

Where the lexer reads the text differently from the FParsec parser, which stage 3 has to settle:

- `true` and `false` are names, as they are not reserved words. The parser takes them as literals where an expression
  or a pattern goes.
- A number has no sign: `-1` is `-` and `1`. The parser reads a sign as part of a literal in some places.
- A `.` is part of a number only when a digit follows it, so `1.` is `1` and `.`. The parser's float literal reads
  `1.` as a float.
- A `{` in any double-quoted string opens a hole. The parser reads a string literal with no holes, as an import's
  path or a pattern's, with `{` as text.

Where the layout pass reads text differently from the FParsec parser, which stage 3 has to settle:

- The parser takes a top-level item at any column, and the layout pass takes a line indented further than the top
  level as a continuation.
- The parser takes the statements of a lambda's block, and a match's arms, at different columns, and the layout pass
  takes a line indented further than the block's first item as a continuation of the item before it.

Only test fragments and a program's output, and one `unformatted` example on the formatter's page, are written that way.

The language server's "am I in code, a string or a comment?" check, doc comments and the reserved-word list could use
tokens now instead of their own scanning.

## Stage 3: the new parser, a part of the grammar at a time

Write the recursive-descent parser over the layout tokens (`Layout.layout`), producing today's syntax tree. It starts
with only part of the grammar, and when it meets something it doesn't handle yet it gives up on the file, and `parse`
falls back to the FParsec parser for that file. So it can land early, with every test still passing through the
fallback.

- **Order:** type expressions first (`Types.fs` is the most self-contained), then patterns, then expressions by
  precedence, then statements and blocks, then top-level definitions, imports and directives.
- **Each step:** port one area, add it to the new parser, and run the differential test. The share of the corpus the
  new parser handles without falling back goes up with each PR, and is reported in it.
- **So far:** the whole grammar: types (`DescentTypes.fs`), patterns (`DescentLiterals.fs`, `DescentPatterns.fs`),
  expressions (`DescentExpressions.fs`), lambdas, blocks, arms and statements (`DescentBlocks.fs`), the expressions that
  start with a keyword (`DescentControl.fs`) and the top level (`DescentTopLevel.fs`), described in
  [The parser](../compiler-internals/parser) in the Compiler internals section. Of the 4,784 pieces of the corpus the
  FParsec parser reads, 4,778 read whole to its tree with no fallback, and none differ. The other 6 are test fixtures
  written as mistakes that the FParsec parser reads leniently (a `match` with no arms, which it reads as a name, and a
  string's hole that doesn't parse, which it reads as text), where the new parser gives up.
- **`parse` reads with it:** `Api.runModuleParser` reads every file with the recursive-descent parser, and with the
  FParsec parser only where that one gives up, so that a mistake is reported as it was. `CorpusTests.fs` checks, on
  every piece of the corpus, that the two read the same items and keep the same side state (the names nodes declare,
  where documentable constructs start, module blocks, comments and string texts), and that the new parser gives up on
  every piece the FParsec parser rejects, so that no mistake goes unreported.
- **Done when** the new parser handles the whole corpus and every parser test with no fallback, and every tree
  matches.

## Stage 4: make it the default

`parse` uses the new parser alone, and reports mistakes itself; the FParsec one stays for a release behind the
environment variable `NANYX_PARSER=fparsec` (see [The parser](../compiler-internals/parser)), so a regression can be
checked against it.

- **Done so far:** the new parser is the default. Where the FParsec parser has a message of its own for a mistake, the
  new parser gives the same message at the same place; anything else is reported at the furthest point it read to,
  with what it expected there and what it found, in place of FParsec's "Expecting: ..." lists. Each test whose
  expected message changed is reviewed, not just updated.
- **Done when** the new parser is the default and a release has gone out with it.

## Stage 5: error recovery

Done: after a mistake the parser leaves out the top-level item it is in, records the error and reads on at the next
top-level item, so every syntax error is reported and the items around them are typed, with hover and completion (see
[The parser](../compiler-internals/parser) in the Compiler internals section).

Still open: recovering inside an item, at the next statement of a block or the next arm of a match, with an error node
in the tree where the mistake was, so the rest of the def being typed keeps its types too. The type checker and the
language server would then learn to skip error nodes. Until then the language server reads the last text that parsed
for the item being typed.

## Stage 6: remove FParsec

Delete the FParsec parser, the fallback and the switch, and the FParsec package. Remove the "FParsec choice order and
`attempt`" rule from [Working in this repository](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md), and say
instead where the layout rules and the reserved words live.

# Open questions

- **A concrete syntax tree.** Once comments and whitespace travel with tokens, the formatter and the refactorings
  could work from a tree that keeps every token and its trivia, instead of re-attaching comments by position. That is
  decided after stage 6, once the refactorings show whether they need one.
