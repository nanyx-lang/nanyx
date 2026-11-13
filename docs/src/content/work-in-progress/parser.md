---
title: "The parser"
description: "Moving the parser from FParsec to a lexer and a hand-written recursive-descent parser, in stages"
order: 34
---

This page is a proposal for how the parser should work, and how to get there from today's parser without a rewrite
all at once. Nothing on it is decided yet: [#846](https://github.com/nanyx-lang/nanyx/issues/846) tracks it, labelled
`needs decision` until the maintainer settles the open questions at the end.

# Today

The parser (`compiler/Nanyx.Parser`, about 3,200 lines, 1,376 of them in `Program.fs`) is written with
[FParsec](https://www.quanttec.com/fparsec/) combinators. It reads characters directly: there is no lexer. It
backtracks with `attempt`, which appears 169 times, and layout (where a block, a match or a pipeline ends) is decided
by the individual parsers, each measuring indentation itself.

That has served the language well, but most of the parser bugs fixed recently come from the approach rather than
the grammar:

- **Keywords leak into names.** With no lexer, a keyword parser can match the start of a longer name: `typeName`
  ([#493](https://github.com/nanyx-lang/nanyx/issues/493)), `trueValue` and `nan`
  ([#521](https://github.com/nanyx-lang/nanyx/issues/521)), `useful`. The reserved words are listed twice, once for
  the parser and once for completion ([#541](https://github.com/nanyx-lang/nanyx/issues/541)).
- **Backtracking hides errors.** An `attempt` that rewinds past the real problem reports it somewhere else, as a
  list error reported at its block's `{` was ([#758](https://github.com/nanyx-lang/nanyx/issues/758)), and FParsec's
  "Expecting: any char in ' ', for loop, function call, ..." messages are the same effect. Because choice order and
  `attempt` placement are behaviour, [Working in this repository](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md)
  has a rule just to protect them.
- **Layout is decided in many places.** A nested match ending at an arm indented back
  ([#249](https://github.com/nanyx-lang/nanyx/issues/249)), a list's `]` on its own line
  ([#248](https://github.com/nanyx-lang/nanyx/issues/248)), `and`/`or` after a call or in a match arm
  ([#617](https://github.com/nanyx-lang/nanyx/issues/617), [#628](https://github.com/nanyx-lang/nanyx/issues/628)) and
  the block after `use ... in` ([#673](https://github.com/nanyx-lang/nanyx/issues/673)) were each a layout case one
  parser handled and another didn't.
- **Comments are kept on the side.** The parser records comment positions in its state
  (`Comments` in `ParseState.fs`); the formatter and doc comments attach them by position afterwards.
- **The first error ends the parse.** "A parse error hides the rest of the file, because the parser has no error
  recovery" ([Language server and editor](./language-server)). The editor gets no outline, no hover and no
  completion below a half-typed line.

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
merge. Each stage below lands on its own, keeps every test passing, and is useful by itself even if the next stage
never happens. The syntax tree (`Ast.fs`) does not change in stages 1 to 4, so the type checker, the backend, the
formatter and the language server need no change until the parser underneath them is replaced.

## Stage 0: prepare

- **A corpus.** Every `.nyx` file in `stdlib/` and `examples/`, the code blocks in `docs/`, and the sources in the
  parser and formatter tests.
- **A differential test.** It parses each corpus file with two parsers and asserts that the syntax trees are equal,
  ignoring node ids. Every later stage is checked by it.
- **The refactors already filed**, which shrink what has to be ported: name the desugared calls in one place
  (done: the `...Syntax` modules in `Ast.fs`, [#367](https://github.com/nanyx-lang/nanyx/issues/367)), keep one list of reserved words
  ([#541](https://github.com/nanyx-lang/nanyx/issues/541)), build the parser's and formatter's operator tables from one
  list (done: `binaryOperators` in `Operators.fs`, [#370](https://github.com/nanyx-lang/nanyx/issues/370)), and split
  the parser out of `Program.fs`
  ([#366](https://github.com/nanyx-lang/nanyx/issues/366)).

## Stage 1: a lexer, used alongside the parser

Write `Lexer.fs`: text in, tokens out, each with its span and its leading and trailing trivia (comments, whitespace,
line breaks). The FParsec parser keeps parsing characters and doesn't use it yet.

- **Done when** the lexer is lossless: joining every token and its trivia gives back the source exactly, for every
  file in the corpus. Its tests cover each kind of token, including string escapes, interpolation, digit separators
  and `---` block comments.
- **Useful at once:** the language server's "am I in code, a string or a comment?" check, doc comments and the
  reserved-word list can use tokens instead of their own scanning.

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

## Later, maybe: a lossless syntax tree

Once comments and whitespace travel with tokens, the formatter could work from a tree that keeps every token and its
trivia (a concrete syntax tree), instead of re-attaching comments by position. That is a separate decision, after
stage 6.

# Open questions

1. **Whether to do this at all, and when.** FParsec works, and the bugs above have been fixed one at a time. The case
   for the move grows with how much the language server matters, because error recovery (stage 5) is what it needs
   most.
2. **Where to stop.** Stages 1 and 2 alone remove the keyword and layout classes of bug, and could stand on their
   own with FParsec reading layout tokens. That isn't recommended, since FParsec is built to read characters and
   loses most of its convenience on tokens, but it is a smaller step.
3. **Error messages that change at stage 4.** Accept the new wording wholesale after review, or keep today's
   messages where tests assert them?
4. **A concrete syntax tree** for the formatter, after stage 6, or not.
