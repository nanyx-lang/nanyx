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
  no completion below a half-typed line.

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

## Stages 0 and 1

Stages 0 and 1 are done: the corpus, the differential test and the lexer, described in
[The parser](../compiler-internals/parser) in the Compiler internals section.

Where the lexer reads the text differently from the FParsec parser, which stage 3 has to settle:

- `true` and `false` are names, as they are not reserved words. The parser takes them as literals where an expression
  or a pattern goes.
- A number has no sign: `-1` is `-` and `1`. The parser reads a sign as part of a literal in some places.
- A `.` is part of a number only when a digit follows it, so `1.` is `1` and `.`. The parser's float literal reads
  `1.` as a float.
- A `{` in any double-quoted string opens a hole. The parser reads a string literal with no holes, as an import's
  path or a pattern's, with `{` as text.

The language server's "am I in code, a string or a comment?" check, doc comments and the reserved-word list could use
tokens now instead of their own scanning.

## Stage 2: the layout pass

Turn the token stream into one with explicit block tokens: an `Indent` where a line starts further in than the line
that opened its block, a `Dedent` where it comes back, and a `Newline` between statements at the same depth. Write
the layout rules down on this page as one list, from what the FParsec parsers do today, including the cases above
(#249, #248, #617, #628, #673).

- **Done when** the block structure the layout pass gives every corpus file matches what today's parser makes of it,
  checked by a test.

## Stage 3: the new parser, a part of the grammar at a time

Write the recursive-descent parser over the layout tokens, producing today's syntax tree. It starts with only part
of the grammar, and when it meets something it doesn't handle yet it gives up on the file, and `parse` falls back to
the FParsec parser for that file. So it can land early, with every test still passing through the fallback.

- **Order:** type expressions first (`Types.fs` is the most self-contained), then patterns, then expressions by
  precedence, then statements and blocks, then top-level definitions, imports and directives.
- **Each step:** port one area, add it to the new parser, and run the differential test. The share of the corpus the
  new parser handles without falling back goes up with each PR, and is reported in it.
- **Done when** the new parser handles the whole corpus and every parser test with no fallback, and every tree
  matches.

## Stage 4: make it the default

`parse` uses the new parser, and the FParsec one stays for a release behind a switch (an environment variable or a
hidden CLI option), so a regression can be checked against it.

- Tests that assert a parse error's message or position will change, mostly for the better. Each changed
  expectation is reviewed, not just updated.
- **Done when** the new parser is the default and a release has gone out with it.

## Stage 5: error recovery

Teach the parser to recover: after an error, skip to the next statement or definition at the same layout depth,
record the error, and carry on. The result is a tree with error nodes in it and every error found, not just the
first.

- The type checker and the language server learn to skip error nodes, so the rest of the file keeps its outline,
  hover, completion and type errors.
- **Done when** a file with a half-typed line in the middle still gets hover and completion below it, with a test.

## Stage 6: remove FParsec

Delete the FParsec parser, the fallback and the switch, and the FParsec package. Remove the "FParsec choice order and
`attempt`" rule from [Working in this repository](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md), and say
instead where the layout rules and the reserved words live.

# Open questions

- **A concrete syntax tree.** Once comments and whitespace travel with tokens, the formatter and the refactorings
  could work from a tree that keeps every token and its trivia, instead of re-attaching comments by position. That is
  decided after stage 6, once the refactorings show whether they need one.
