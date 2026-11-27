---
title: "Formatter: a Nanyx package"
description: "Moving the formatter out of the compiler into a shipped package written in Nanyx"
order: 30
---

How the formatter lays code out is described for users on [Formatter](../standard-tools/formatter), and how it is built
on [Formatter](../deep-dives/formatter) in Deep dives. Today it is F#, in `compiler/Nanyx.Formatter`, about 3,200
lines. This page is the design for moving its printer into a shipped package written in Nanyx, `nanyx.format`, as the
official linter is moving into `nanyx.lint` ([Linters: compiler design](linters)). It builds on that design and
doesn't repeat it: a host package, a build cached by SHA-256 and the compiler's version, one instance kept and reused,
the tree encoded into its memory. The [open questions](#open-questions) are the maintainer's to decide.

The linter goes first and the formatter second, before any part of the compiler itself: both are tools at the
compiler's edge, written against a contract, so they test the language and its runtime on real programs without the
compiler depending on them to build.

# What is decided

Decided by the maintainer:

- There is one official formatter, as there is now. A project doesn't choose a formatter package or pin a version of
  one: the compiler runs the `nanyx.format` it ships, at its own version.
- The compiler still parses. The package is given the module's syntax tree, its comments and blank lines, and its
  text, and gives back text. It has no parser of its own.
- Formatting doesn't type-check, as today, so a module with type errors formats. The formatter gets no `Analysis`
  context.
- The tree is the linter's, in a package of its own, `nanyx.syntax`, with the comments and blank lines beside it
  ([The tree](#the-tree)).
- The check that formatting changed nothing but layout stays in the compiler, in F#
  ([Keeping its guarantees](#keeping-its-guarantees)).
- The F# printer and the package run side by side until they write the same text for every file in the corpus, and
  the package meets the [performance bar](#performance) ([Stages](#stages)).

# The tree

The formatter needs everything the linter's tree has, the items as written with their spans and doc comments, and also
what the parser skips: ordinary comments, `---` block comments and runs of blank lines. It doesn't need anything type
checking finds out.

The parser already keeps these apart from the tree. `SourceModule` (`SourceTrivia.fs`) is the items as written, with
`module name =` blocks kept as they are, a side list of `Trivia` in source order, each with its span, and the text the
spans index into. The F# formatter attaches each comment to a node itself (`CommentPlacement.fs`). So the package takes
the same three things, and the tree is the linter's, not a second one:

```nanyx fragment
export type Trivia =
  | #lineComment(text: string, trailing: bool, span: Span)
  | #docComment(text: string, span: Span)
  | #blankLines(count: int, span: Span)

export type SourceModule = (items: list(TopLevelItem), trivia: list(Trivia), source: string)
```

The tree's types move out of `nanyx.analysis` into a package of their own, `nanyx.syntax`: the node kinds, `Span`,
`NodeId`, `Trivia` and `SourceModule`, and the decoding of the tree from the host's encoding. `nanyx.analysis` depends
on it and adds `Analysis`, types, symbols and reports. A linter sees no difference; the formatter depends on
`nanyx.syntax` alone, and so doesn't change when the `Analysis` queries do. A node's `id` is still there; the formatter
ignores it.

One tree for both, rather than a tree of the formatter's own, because they would be the same tree but for the trivia,
both change every time the syntax does, and two encodings of it would drift. The trivia is a side list rather than
fields on the nodes because that is what the parser makes, and because only the formatter wants it: the linter's host
doesn't encode it.

Two things the formatter reads that aren't in the tree's shape come from the text through spans, as now: a literal as
written (`0xFF`, `1_000`, escapes), and whether a list was written as a table, `[|`.

# The contract

```nanyx fragment
export module nanyx.format

import nanyx.syntax/syntax as (SourceModule)

export type Options = (lineWidth: int, indentSize: int)

export def format: Options, SourceModule -> string = { options, m ->
  ...
}
```

Source text and options in, text out. The text out is the whole module, with `\n` line endings and no trailing
whitespace; the host puts back the file's own line endings, as `FormatCommand` does now. There are no edits and no
diagnostics: what formatting changed, and whether it was safe to, is the host's to work out.

## Three packages

| Package | What it is |
|---------|------------|
| `nanyx.syntax` | The syntax tree, its trivia and its decoding, shared with `nanyx.analysis`. |
| `nanyx.format.host` | The formatter host. It reads the module through `nanyx:format`, an import of the runtime's own, runs `format`, and writes the text back. |
| `nanyx.format` | The formatter: `format` and the document printer (`Doc.fs` today) it is written with. |

All three are shipped, at the compiler's version, under `packages/`. `nanyx.format`'s format build, its `format` passed
to the host's `run`, is built when the package is seeded and kept in the package cache by its SHA-256 and the
compiler's version, as a lint build is.

## Who calls it

`compiler/Nanyx.Formatter` becomes the host's F# side: it reads the settings, parses, encodes the module, runs the
format build, and checks what comes back. Everything that calls the formatter today calls it there, through one
function from options, file name and text to `Formatting` (`FormatCommand.formatText`), so none of them changes:

- **`nanyx format`** and **`format --check`** run it on each file, in parallel as now, one instance per thread.
  `--stdin` is the same with the text read from standard input.
- **The language server**'s `textDocument/formatting`, `rangeFormatting` (which formats the whole document and takes
  the lines of the items the range touches, `RangeFormatting.formatRange`) and its format hints all read the one
  formatted text, `FormattingHandler.FormattedText`, so they still never disagree. The instance is kept for the
  session, so a keystroke costs an encode and a call.
- **The info view**'s signatures (`SignatureLayout.layOut`) format `def name: type = 0` at the view's width, as now.
- **The refactorings** in the compiler's core format the code they write (`Refactoring.formatted`), and `nanyx new`
  formats the files it writes (`PackageCommands`). The core doesn't reference the Wasm backend or the runtime, so, as
  with linters, it takes the formatter as a function, which the CLI and the language server make from the package.
- **The tests**: the formatter's tests in `Nanyx.Formatter.Tests`, the corpus test over the examples, the docs' code
  blocks and the home page, and `StyleExampleTests`, call the same function, and so run the package. They stay where
  they are, with the same inputs and expected text.

## Configuration

The settings are read by the host, as now: the `formatter` field of the nearest `.nyxp` (`Settings.read`), or a
script's `::format` directives, checked as they are today, with the same errors. The package is given only the result,
`Options`. A setting the formatter gains is added to `Options`, to `Settings.read` and to the user's page together.

# Keeping its guarantees

`nanyx format` never changes what a program means, and formatting is idempotent. Both stay where they are, outside the
package:

- **The check is the host's.** After `format` returns, the host parses its text with the compiler's parser and
  compares the tree with the original, with imports sorted and comments compared as they are now (`Syntax.normalize`,
  `FormatCommand.commentsOf`). If they differ, or the text doesn't parse, the file is left alone and reported as now
  (`NotSafe`). The printer can be wrong; it can't be wrong silently, because what checks it isn't written in it.
- **A formatter that fails is a formatter that refused.** A trap, running out of fuel, or text that isn't valid UTF-8
  is reported as an internal error naming the file, the file is left alone, and `nanyx format` exits with `2`. The
  language server answers with no edits and no hints.
- **Idempotence** stays a test over the repository's Nanyx and the docs, now of the package.

# Bootstrapping

`nanyx.format` is compiled by the F# compiler like every shipped package, at the same version: the compiler of a
release builds the formatter of that release when it is seeded. Nothing compiles itself, so there is no bootstrap
compiler and no earlier release to build with.

What is circular is formatting: the package's own source, under `packages/nanyx.format`, is checked by
`nanyx format --check` in CI with the formatter built from that same source. That is safe:

- Building the formatter doesn't need its source to be formatted, so a formatter that is broken still builds.
- A change that breaks the printer can't damage its own source, or anything else: what it writes that means something
  different is refused by the host's check, and a trap leaves the file alone. CI's `format --check` and the corpus test
  fail, naming the files, and the fix is to the printer.
- A change to the style reformats the corpus, its own source included, in the same PR, as a change to the F# formatter
  does today.

There is no fallback to a previous formatter: a broken formatter is fixed forward.

A change to the syntax now touches the parser (F#), `nanyx.syntax`'s types and its decoding, the encoder in the host,
and the printer (Nanyx) in one PR, as the parser and the F# formatter change together today. A change to
`nanyx.syntax` changes the linter contract's version too ([Versioning](linters#versioning)).

# Performance

The F# formatter is the baseline: `nanyx format --check ../stdlib` (31 files, about 9,900 lines) takes about 1.1 s
on a warm run of a Release build, starting the process included. The package is compared with it two ways:

- **Time**, as a Timing test (`[<Trait("Category", "Timing")>]`, run nightly): format every file under `stdlib/` with
  each formatter, in process, warm, and compare the medians of several runs.
- **Work**, in CI: the fuel the package spends on `stdlib/` (`WasmProgram.Work`), with a budget, so that a change that
  makes it much slower fails a test. Fuel doesn't compare with F#, so this guards against regressions, not the
  baseline.

Fast enough means:

- `nanyx format --check` on `stdlib/` takes no more than twice as long as with the F# formatter, building the format
  build aside, since that is done once, when the package is seeded;
- in the editor, the largest file in `stdlib/` (`prelude-maps.nyx`, about 1,300 lines) formats in under 50 ms, since the
  format hints run as the user types.

The F# formatter isn't deleted until both hold.

# Stages

The F# printer and the package run side by side until the package writes exactly what the F# printer does, so that a
difference is a bug in the port, found by a test:

1. **`nanyx.syntax`.** The tree's types move out of `nanyx.analysis` into a package of their own, with `Trivia` and
   `SourceModule`, and the host encodes trivia when the tree is for a formatter. This lands with or after the linter's
   contract ([#3171](https://github.com/nanyx-lang/nanyx/issues/3171)).
2. **The host.** `nanyx.format.host` and the `nanyx:format` import, format builds and their cache, and
   `compiler/Nanyx.Formatter` running a format build behind `FormatCommand.formatText` with the safety check, behind a
   setting that chooses it; the refactorings take the formatter as a function. A test formatter checks it end to end.
3. **The printer.** `nanyx.format` under `packages/`, at the compiler's version: the document printer first, then the
   printers a construct at a time, as the F# files are split (types, patterns, imports, calls, lambdas, branches,
   operators, the rest). A differential test formats every file in the corpus with both and expects the same text; it
   lists the files they differ on until there are none.
4. **The switch.** The package is the formatter for `nanyx format`, the language server, the refactorings and the
   tests, once the differential test passes and the performance bar is met.
5. **The F# printer goes.** `compiler/Nanyx.Formatter` keeps the host, settings, file finding, range formatting and
   the safety check; the printer's files and the differential test are deleted, and the Deep dives page describes the
   package.

# Open questions

- **Third-party formatters.** A project can't choose another formatter. Whether the formatter should take settings of
  its own beyond `lineWidth` and `indentSize`, or a project another formatter, is left as it is: no.
- **Range formatting in the package.** The host formats the whole document and takes the lines it wants. A `format`
  that takes a range could skip the work outside it, if whole-document formatting turns out too slow in the editor.
