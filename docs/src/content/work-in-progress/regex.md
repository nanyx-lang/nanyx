---
title: "Regular expressions"
description: "Checking regex patterns at compile time, and where regular expressions go next"
order: 7
---

The shipped package `nanyx.regex`, with its `#Matches` and `#MatchesRegex` patterns for `match` arms, is described on
its [reference page](../stdlib/regex) and in the [cookbook](../cookbook/regular-expressions), and how it is built, and
why, on [Shipped packages](../deep-dives/shipped-packages#why-regular-expressions-are-built-this-way). This page keeps
what is left: checking literal patterns at compile time, and the open questions.

# Patterns checked at compile time

A bad pattern written as a literal should be an error where it's written, not a crash when it runs. Nanyx doesn't
evaluate code at compile time, so the check is to be a linter rule that calls the package's own `Regex.parse` on each
literal passed to it, or to `#Matches`, and reports the `RegexError` at the column inside the literal. A pattern built
at run time still gives `#invalidPattern`. No compiler special case is needed.

This waits for linters written in Nanyx ([#107](https://github.com/nanyx-lang/nanyx/issues/107),
[#1343](https://github.com/nanyx-lang/nanyx/issues/1343)): the [linters](../advanced/linters) are F#, compiled into the
compiler, and can't run Nanyx code. Porting the pattern reader to F# would make a second parser that drifts from the
package's, and compiling and running the package from the F# linter would be machinery thrown away once the contract
exists. Until then, a wrong literal gives `#invalidPattern` from `Regex.parse`, and `#Matches` crashes with it.

# Open questions

- **Parser combinators.** A `nanyx.parse` package is often the better tool for structured text, and reads well with
  pipes and workflows. It would complement regexes, not replace them.
- **Where it lives** if the standard library's public modules are reorganised
  ([#2029](https://github.com/nanyx-lang/nanyx/issues/2029)): it fits under `nanyx/text` if it moves into the
  standard library.

# Plan

| Step | Issue | After |
|---|---|---|
| A linter rule that checks literal patterns at compile time | [#2033](https://github.com/nanyx-lang/nanyx/issues/2033) | #107, #1343 |
