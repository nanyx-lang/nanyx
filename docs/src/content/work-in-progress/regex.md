---
title: "Regular expressions"
description: "A linear-time regex package written in Nanyx, patterns checked at compile time, and regexes in match arms"
order: 7
---

This page is the design of regular expressions, agreed with the maintainer on 2026-11-17 and filed in three stages
([Plan](#plan)). The first is built: the shipped package `nanyx.regex`, described on its [reference page](../stdlib/regex)
and in the [cookbook](../cookbook/regular-expressions). Checking literal patterns at compile time and matching
regexes in `match` are still to come.

# Decisions

- **A shipped package, `nanyx.regex`,** named in a manifest's `dependencies` or a script's `::dependency`, as
  [`nanyx.json`](./files-http-json) is. The standard library stays what every program needs.
- **Written in Nanyx, not provided by a host.** Matching is pure computation, so it needs no capability, and taking it
  from the host (.NET's engine in the console host, JavaScript's in a browser) would give each host different
  semantics. The engine is also a real workload for `MArray`, functional but in place and sequences, written with the
  features users have.
- **Linear time, as RE2, Go and Rust do.** The engine is a Thompson NFA run as a Pike VM, so matching takes time
  linear in the input for every pattern, and no pattern can hang a server (ReDoS). The price is the same as theirs: no
  backreferences and no lookaround.

# The API

```nanyx fragment
::dependency nanyx.regex

def digits = Regex.parse('[0-9]+') \orCrash("bad pattern")   -- #some(Regex) | #error(RegexError)

"abc123" \Regex.isMatch(digits)                  -- true
"a1b22" \Regex.find(digits)                      -- #some(Match) | #notFound
"a1b22" \Regex.findAll(digits)                   -- seq(Match), lazy
"a1b22" \Regex.replace(digits, with = "#")       -- "a#b#"
```

- **`Regex` is a `protected` type** that only `Regex.parse` makes, so a value of it always holds a pattern that
  compiled. Patterns are usually written as raw, single-quoted strings, so backslashes need no escaping.
- **The syntax is RE2's subset:** classes, anchors, capturing, non-capturing and named groups, alternation, greedy and
  lazy quantifiers, and the `i`, `m` and `s` flags. Backreferences and lookaround are a `RegexError` that says
  they're not supported.
- **Text is matched by Unicode code point,** and a `Match` gives its `text`, `start`, `end`, and each group by number
  or name as `#some(string) | #none`.
- **The functions take the text first,** so they pipe: `isMatch`, `find`, `findAll`, `replace`, `replaceWith` and
  `split`.

# Patterns checked at compile time

A bad pattern written as a literal should be an error where it's written, not a crash when it runs. Nanyx doesn't
evaluate code at compile time, but its [linters](../advanced/linters) run Nanyx code at compile time, so a linter rule
calls `Regex.parse` on each literal passed to it and reports the `RegexError` at the column inside the literal. A
pattern built at run time still gives `#error`. No compiler special case is needed.

# Regexes in `match`

[Custom patterns](../advanced/custom-patterns) with arguments let the package offer a pattern that matches a regex,
with the match as its payload:

```nanyx fragment
match line
  | #Matches('^\s*$') -> #blank
  | #Matches('^(\d+)-(\d+)$')(m) -> #range(m \group(1), m \group(2))
  | _ -> #other
```

The argument is parsed once, when it's given, since a pattern's body is curried.

# Alternatives considered

- **Taking the host's engine,** through a runtime import as `Http` does. Fast and complete, but each host would match
  differently, and pure code would need a capability.
- **A backtracking engine,** with backreferences and lookaround. More features, but exponential time on some
  patterns.
- **Typed captures,** where a pattern's named groups become a record type, `(year: string, month: string)`. That
  needs the compiler to evaluate the pattern while type checking, which Nanyx deliberately doesn't do.

# Open questions

- **Parser combinators.** A `nanyx.parse` package is often the better tool for structured text, and reads well with
  pipes and workflows. It would complement regexes, not replace them.
- **Where it lives** if the standard library's public modules are reorganised
  ([#2029](https://github.com/nanyx-lang/nanyx/issues/2029)): it fits under `nanyx/text` if it moves into the
  standard library.

# Plan

| Step | Issue | After |
|---|---|---|
| `nanyx.regex`: the engine and the API (done) | [#2032](https://github.com/nanyx-lang/nanyx/issues/2032) | |
| A linter rule that checks literal patterns at compile time | [#2033](https://github.com/nanyx-lang/nanyx/issues/2033) | #2032 |
| `#Matches`, a custom pattern for `match` arms | [#2034](https://github.com/nanyx-lang/nanyx/issues/2034) | #2032 |
