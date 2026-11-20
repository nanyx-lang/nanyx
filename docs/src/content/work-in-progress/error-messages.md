---
title: "Error messages"
description: "What is still open about what diagnostics say and where they point"
order: 28
---

How errors are shown, their suggestions and fixes, and every code are described in
[Compiler errors](../standard-tools/errors), and how a message is worded in
[Principles](../foundations/principles#friendly-errors). This page keeps what is still open in the error messages
workstream. Work is tracked under the `error messages` label.

Every compiler error has a code and title from the table in `Diagnostics.fs` (`DiagnosticCodes`), the span of the node
it is about, and a message in the house style. The CLI shows it with the line of source and an underline, and the
common errors offer fixes. What is left:

- **Rewriting the messages** ([#2098](https://github.com/nanyx-lang/nanyx/issues/2098)). Every error is shown as a
  headline, its code's fixed title plus at most one name in single quotes (`Missing context 'Console'`), kept as the
  diagnostic's `Subject`, and a body that doesn't repeat it. The editor shows the headline, then the body on the next
  line; the terminal shows the code and headline, then the underlined source line, then the body. The codes for
  reading a module (NYX00xx) and resolving names (NYX01xx), and NYX0205, are written this way. The other messages are
  rewritten to it a phase at a time: types ([#2134](https://github.com/nanyx-lang/nanyx/issues/2134)), and the rest
  ([#2135](https://github.com/nanyx-lang/nanyx/issues/2135)). Until then, many bodies still start with their headline.
  A syntax error's headline has no name yet: the parser's errors carry only a message, so the token it found isn't
  known apart from it.
- **Related locations.** Few errors use them yet. An error should point, where it helps, at the code that explains it
  as well: "expected because of this annotation", "first defined here". The CLI shows a related location as one line,
  with no excerpt.
- **Where a type mismatch points.** A mismatch is reported where the constraint that failed was made, which is not
  always where the conflicting types came from (see [Deferred errors](./deferred-errors#open-questions)).
- **Field names.** The syntax tree keeps no span for a record field's name, so an error about one is reported at the
  nearest node that has a span.
- **Syntax errors.** A syntax error the parser has no message of its own for says what it expected at the furthest
  point it read to and what it found there, `Expected `]`, found `xs``. Where several readings failed there, the list
  of what they expected can be long; error recovery ([#846](https://github.com/nanyx-lang/nanyx/issues/846), stage 5)
  is where these get better.
- **Nominal types.** A mismatch with a nominal type outside its module is explained only when the shapes match
  ([#1804](https://github.com/nanyx-lang/nanyx/issues/1804)).

# Open questions

- Are error message snapshots part of the compiler tests, so wording changes are reviewed deliberately?
