---
title: "Regular expressions"
description: "Checking regex patterns at compile time, and where regular expressions go next"
order: 7
---

`nanyx.regex`, with its `#Matches` and `#MatchesRegex` patterns for `match` arms, is a package from the index,
published from [nanyx-lang/regex](https://github.com/nanyx-lang/regex), whose README says how its engine is built and
why. It is described on its [reference page](../stdlib/regex) and in the [cookbook](../cookbook/regular-expressions).
This page keeps what is left for the compiler: checking literal patterns at compile time, and the open questions.

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

# Plan

| Step | Issue | After |
|---|---|---|
| A linter rule that checks literal patterns at compile time | [#2033](https://github.com/nanyx-lang/nanyx/issues/2033) | #107, #1343 |
