---
title: "Error messages"
description: "What diagnostics say and how they are shown"
order: 28
---

> **Status: outline.** This page frames the error messages workstream: what it covers, where the compiler is today
> and what comes next. Sections marked _to do_ are placeholders to fill in as the work is designed. Work is tracked
> under the `error messages` label.

# Summary

[Principles](../foundations/principles#human-readable-errors) promises errors with a code,
a short title, a detailed message using the program's own names, suggestions, and fixes an editor can apply,
written in a matter-of-fact passive voice. This workstream is about meeting that promise: what each diagnostic
says, where it points, and how the CLI and the editor show it.

# Scope

- The catalogue of compiler errors: their codes, titles and wording.
- Positions: every error points at the code it is about, in the right file.
- Suggestions and fixes for the common errors ("did you mean", missing context, missing match case).
- Rendering in the CLI (`nanyx check`, `nanyx run`) and in the language server.
- A reference page in the docs with a section for each error code.

The `Diagnostic` type itself (code, span, related locations, fixes) is designed in
[Linters: structured diagnostics](./linters#2-structured-diagnostics) and tracked there, because linters
need it first. This workstream fills it with content.

# Where the compiler is today

- **Diagnostics are structured** ([#97](https://github.com/nanyx-lang/nanyx/issues/97)): every compiler error has
  a code and title from the table in `Diagnostics.fs` (`DiagnosticCodes`), and the span, in its own file, of the
  node it is about. Few use related locations yet.
- **Some spans are approximate.** Field names, type annotations and attributes have no spans yet
  ([#96](https://github.com/nanyx-lang/nanyx/issues/96)), so errors about them are reported at the nearest node
  that has one. A type mismatch is reported where the constraint that failed was made, which is not always where
  the conflicting types came from. A mismatch between two records or two tag unions names what only one of them
  has: `Tag union mismatch: only the first has #bad; only the second has #underflow.`
- **A pipe that finds nothing to call says what it looked for** (`PipeLookups.fs`). On a value whose type is not
  known yet, it names the attached definitions it could not choose between. On a known type, it says the type has
  no such attached definition and no plain function has the name, and where the definition is if the pipe cannot
  reach it: under an `as` alias (`'string.trim' is available as 't.string.trim'`), or in a standard library module
  that is not imported (`'string.trim' is defined in 'nanyx/text'`); failing those, it names what other types
  attach under the name (`Only 'list.shout' is in scope`). A plain function of the name that does not take the
  value is a `NYX0201` on the name, saying what it takes (`the function 'shout' in scope takes 'int', not
  'string'`), rather than a mismatch over the whole pipe. A definition two imports attach is `NYX0106`, naming both
  modules.
- **Wording is inconsistent.** Messages range from `Unknown identifier 'x'` to `dbg expects a single argument`
  and `` `with` updates fields by name``; quoting, capitalisation and voice vary.
- **The CLI shows each diagnostic with its source** ([#880](https://github.com/nanyx-lang/nanyx/issues/880)):
  `file:line:col: error NYX0201: message`, then the line the span starts on, after its number, with the span
  underlined in carets (to the end of that line when the span goes on past it), as `SourceExcerpt.cs` lays it out.
  On a terminal that takes ANSI escapes, and when `NO_COLOR` is not set, the severity and the underline are coloured.
  A related location is still one line, with no excerpt.
- **Some errors have fixes.** An unknown name that a standard library module exports offers to import it, and a
  named parameter passed by position (NYX0218) offers to name the arguments when each fits just one parameter.
- **An unknown name asks "did you mean"** (`NameSuggestions.fs`): the names in scope closest to it by edit distance
  (a swap of two letters counts as one edit), at most one edit for every three characters, are named in the message
  (`Unknown identifier 'totl': did you mean 'total'?`), each with a fix that writes it. A dotted name is compared
  only with names that have the same qualifier, and a name the compiler reserves (`__for`, `__nanyx_exit`) is never
  offered.
- **A match that is not exhaustive offers the arms it is missing** (`PatternSpace.missingArms`, laid out by
  `MatchArmFixes.fs`): each with `???` for a body, after the last arm and indented as it is, or on the same line
  when the arms are written on one. A tag with a payload is added as `#some(_)`, and an int, string or float as `_`,
  since those have too many values to name. A match that would take more than a dozen arms, or whose arms have no
  `|` (a lambda's pattern parameters), gets no fix.
- **A def that needs a context its type does not declare offers to add it** (`ContextFixes.fs`): every error that
  says "add [C] to the type of 'f'" (an operator, `++`, an interpolation hole, a call, an ambient context such as
  `Console`) has a fix that writes `C` after the contexts the type writes, joined with `&`, or in front of the type
  as `[C] ` when it writes none. It reads where the type is written from `InferState.WrittenTypes`. A context the
  def could load with `use` instead is only named in the message.

# The error reference

[Compiler errors](../standard-tools/errors) is one hand-written page with a section for every code in `DiagnosticCodes`,
headed `# NYX0201: Type mismatch`, which says when the error is reported, shows a short program that reports it, and
says how to fix it. A heading that starts with a code gets the code as its anchor (`#nyx0201`), from
`rehypeDiagnosticCodeIds` in `docs/site/rehype-plugins.mjs`. Settled in
[#882](https://github.com/nanyx-lang/nanyx/issues/882), over a generated page per code, whose generated part would say
little beyond the title.

`ErrorReferenceTests.fs` in `Nanyx.Compiler.Wasm.Tests` keeps the page and the compiler together:

- The page's sections are the codes of `DiagnosticCodes.all`, in that order, each with the compiler's title. A new
  code fails the test until it has a section, and so does a section for a code that no longer exists.
- Every ` ```nanyx ` block is compiled as a script, and must report errors of its section's code and no other. The
  backend compiles it too when the type checker finds nothing, so `NYX03xx` examples work the same way. A block of
  several files starts each with a line `-- file: <name>`, and the first is the one compiled; a file `app.nyxp`
  among them makes the others a package. The fences `fragment` and `skip=#<issue>` mean what they do in
  [the tour](./compilation#tour-examples).

The language server will set each diagnostic's `codeDescription.href` to the code's section on nanyx.dev, so that an
editor shows the code as a link.

# Goals

- Every compiler error has a stable code and a title, and follows the voice in Principles.
- Every error carries a span and, where it helps, related locations ("expected because of this annotation").
- The CLI shows the file, the line of source and a marker under the span.
- The most common errors (unknown name, type mismatch, missing context, non-exhaustive match) offer a
  suggestion, and a fix where the edit is unambiguous.

# Plan

_To do._ Candidate items, not yet filed:

1. **Inventory.** List every error the parser and type checker can produce, with an example program for each.
2. **Codes and wording.** Allocate codes and rewrite messages to the house style, as each error moves to the
   structured `Diagnostic` (after [#97](https://github.com/nanyx-lang/nanyx/issues/97)).
3. ~~**CLI rendering.** File, source excerpt and underline; colour when the output is a terminal.~~ Done
   ([#880](https://github.com/nanyx-lang/nanyx/issues/880)); see Where the compiler is today.
4. ~~**Suggestions.** "Did you mean" for unknown names; missing context and missing match cases, with fixes.~~
   Done ([#881](https://github.com/nanyx-lang/nanyx/issues/881)); see Where the compiler is today.
5. **Error reference.** One page with a section per code, checked against the compiler's table, its examples compiled as
   tests, and linked from the language server ([#882](https://github.com/nanyx-lang/nanyx/issues/882)); see
   [The error reference](#the-error-reference). The page and its test are in; the sections for `NYX02xx` and `NYX03xx`
   and the language server's link are still to do.

# Open questions

- One flat `NYX0001` sequence, or ranges per phase? (Also open in [Linters](./linters#open-questions).)
- Are error message snapshots part of the compiler tests, so wording changes are reviewed deliberately?
