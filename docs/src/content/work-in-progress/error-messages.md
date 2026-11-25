---
title: "Error messages"
description: "What is still open about what diagnostics say and where they point"
order: 28
---

How errors are shown, their suggestions and fixes, and every code are described in
[Compiler errors](../standard-tools/errors), and how a message is worded in
[Principles](../meta/principles#friendly-errors). This page keeps what is still open in the error messages
workstream. Work is tracked under the `error messages` label.

# Still open

- **A syntax error's headline.** Every error's headline is its code's title plus at most one name, but a syntax
  error's has no name: the parser's errors
  carry only a message, so the token it found isn't known apart from it.
- **Related locations.** Few errors use them. An error should point, where it helps, at the code that explains it
  as well: "expected because of this annotation", "first defined here". The CLI shows a related location as one line,
  with no excerpt.
- **Where a type mismatch points.** A mismatch is reported where the constraint that failed was made, which is not
  always where the conflicting types came from (see [Deferred errors](./deferred-errors#open-questions)). A `match`
  whose arms, an `if` whose branches, or a list whose items differ is reported at the whole of it, not at the one that
  differs ([#2688](https://github.com/nanyx-lang/nanyx/issues/2688)).
- **Field names.** The syntax tree keeps no span for a record field's name, so an error about one is reported at the
  nearest node that has a span.
- **Syntax errors.** A syntax error the parser has no message of its own for says what it expected at the furthest
  point it read to and what it found there, `Expected `]`, found `xs``. Where several readings failed there, the list
  of what they expected can be long. Recovering inside an item ([The parser](./parser#recovering-inside-an-item)) is
  where these can get better.
- **Nominal types.** A mismatch with a nominal type outside its module is explained only when the shapes match
  ([#1804](https://github.com/nanyx-lang/nanyx/issues/1804)).

# Open questions

- Are error message snapshots part of the compiler tests, so wording changes are reviewed deliberately?
