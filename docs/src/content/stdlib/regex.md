---
title: regex
description: Regular expressions that take linear time
order: 15.95
---

`nanyx.regex` isn't part of the standard library, but the compiler ships it: an application lists it in its manifest's
`dependencies` (`nanyx install nanyx.regex` adds it), and a script names it in a `::dependency nanyx.regex` line. Import
`nanyx.regex/regex`, and call its functions through the type they are attached to, `Regex.find`, or pipe a text into
them, `text \Regex.find(digits)`. It is pure, so it needs no context. The [cookbook](../cookbook/regular-expressions)
shows it at work.

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

match Regex.parse('(\d+)-(\d+)')
  | #some(range) ->
    match "pages 10-20" \Regex.find(range)
      | #some(m) -> println("{m.text} from {m.start}: {m \group(1)} to {m \group(2)}")
      | #notFound -> println("no range")
  | #invalidPattern(e) -> println("bad pattern at {e.column}: {e.message}")

-- 10-20 from 6: #some(10) to #some(20)
```

A pattern that is wrong is a `RegexError`, with the column it goes wrong at and why, so a pattern built at run time
can be checked before it is used:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

match Regex.parse('(\d+')
  | #some(_) -> println("ok")
  | #invalidPattern(e) -> println("{e.column}: {e.message}")

-- 1: There is no ')' to close this '('
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Regular expressions: patterns that find, test, replace and split text.

[`Regex.parse`](#regexparse) reads a pattern into a [`Regex`](#regex), or says where it is wrong and why, as an
`#invalidPattern` carrying a [`RegexError`](#regexerror). The functions attached to `Regex` take the text
first, so they pipe: `"a1b22" \Regex.find(digits)`. [`Regex.isMatch`](#regexismatch) says whether a regex matches anywhere in a text,
[`Regex.find`](#regexfind) finds the first match, [`Regex.findAll`](#regexfindall) each in turn, [`Regex.replace`](#regexreplace) and
[`Regex.replaceWith`](#regexreplacewith) replace them, and [`Regex.split`](#regexsplit) splits a text where they are. A program
names `nanyx.regex` in its manifest's `dependencies`, or a script in a `::dependency` line, and
imports it as `import nanyx.regex/regex`.

A pattern is usually written as a raw, single-quoted string, `'\d+'`, so its backslashes need no
escaping. The syntax is RE2's: characters, and `\` before any that is syntax; `.`; classes such
as `[a-z]`, `[^,;]` and `[[:alpha:]]`; the classes `\d`, `\w` and `\s`, and `\D`, `\W` and `\S`
for what they leave out; the escapes `\n`, `\t`, `\r`, `\f`, `\v`, `\a`, `\x41` and `\x{1F600}`;
the anchors `^`, `$`, `\A` and `\z`, and `\b` and `\B` for a word's edge; groups `( )`,
non-capturing `(?: )` and named `(?<name> )`; alternatives with `|`; and the quantifiers `*`,
`+`, `?`, `{m}`, `{m,}` and `{m,n}`, each greedy, or lazy with a `?` after it. The flags `i`
(case-insensitive), `m` (`^` and `$` match at each line's start and end) and `s` (`.` matches a
line feed too) are turned on with `(?i)`, off with `(?-i)`, for the rest of the group they're
in, or for a group of their own with `(?i: )`.

Matching takes time in proportion to the text's length for every pattern, as RE2's, Go's and
Rust's does, so no pattern can make a program hang. The price is theirs too: backreferences and
lookaround aren't supported, and each is a `RegexError` that says so.

Text is matched by Unicode code point, so `.` matches one character however many bytes it is,
and a [`Match`](#match)'s `start` and `end` count code points. `\d`, `\w`, `\s` and `\b` are ASCII's, as
in RE2, and `(?i)` matches both cases of the ASCII, Latin-1, Greek and Cyrillic letters.

## `Regex`

```nanyx fragment
type Regex = protected (pattern: string, program: Program)
```

A pattern that has been read, and compiled into a program to match it with. Only
[`Regex.parse`](#regexparse) makes one, so a `Regex` always holds a pattern that's right. `pattern` is the
pattern as it was written, and `program` is what the package matches it with.

## `RegexError`

```nanyx fragment
type RegexError = (column: int, message: string)
```

Why [`Regex.parse`](#regexparse) made no regex: the column of the pattern where it goes wrong, counting code
points from 1, and what is wrong there.

## `Regex.parse`

```nanyx fragment
def Regex.parse: string -> #some(Regex) | #invalidPattern(RegexError)
```

The regex of `pattern`, or `#invalidPattern` with where it goes wrong and why.

## `Regex.toString`

```nanyx fragment
def Regex.toString: Regex -> string
```

A regex's pattern, as it was written.

## `Match`

```nanyx fragment
type Match = protected (text: string, start: int, end: int, groups: array(#some(string) | #none), names: list((string, int)))
```

Where a regex matched: the text it matched, and the code points it starts at and ends before,
counting from 0. [`Match.group`](#matchgroup) and [`Match.named`](#matchnamed) give what its groups matched.

## `Match.group`

```nanyx fragment
def Match.group: Match, int -> #some(string) | #none
```

What group `number` of the match matched, counting the groups' `(`s from 1, with group 0 the
whole match, or `#none` when the group took no part in the match, or there is no such group.

## `Match.named`

```nanyx fragment
def Match.named: Match, string -> #some(string) | #none
```

What the group named `name`, as `(?<name> )` names it, matched, or `#none` when it took no part
in the match, or there is no group of that name.

## `Regex.isMatch`

```nanyx fragment
def Regex.isMatch: string, Regex -> bool
```

Whether `regex` matches anywhere in `text`.

## `Regex.find`

```nanyx fragment
def Regex.find: string, Regex -> #some(Match) | #notFound
```

The first match of `regex` in `text`: the leftmost, and of those that start there, the one the
pattern prefers, as a backtracking engine would find it.

## `Regex.findAll`

```nanyx fragment
def Regex.findAll: string, Regex -> seq(Match)
```

Each match of `regex` in `text`, in turn, as a sequence that finds each only when it is pulled.
Each starts where the last ended; an empty match right where the last ended is skipped, and
after an empty match the next is looked for a character further on.

## `Regex.replaceWith`

```nanyx fragment
def Regex.replaceWith: [c] string, Regex, ([c] Match -> string) -> string
```

`text` with each match of `regex` replaced by what `replacement` gives for it.

## `Regex.replace`

```nanyx fragment
def Regex.replace: (string, Regex, with: string) -> string
```

`text` with each match of `regex` replaced by `with`, in which `$1` stands for what group 1
matched, `$0` for the whole match, `${name}` for what the group named `name` matched, and `$$`
for a `$`. A group that took no part in the match stands for nothing.

## `Regex.split`

```nanyx fragment
def Regex.split: string, Regex -> list(string)
```

The pieces of `text` between the matches of `regex`, in order. An empty match at the very start
splits nothing off, so `"abc" \Regex.split(empty)` is `["a", "b", "c"]`, and an empty text is one
empty piece.

## `#Matches`

```nanyx fragment
pattern string -> string -> #Matches(Match) | _
```

Matches a string that the regex of `pattern` matches anywhere in, with the [`Match`](#match) as its
payload: `| #Matches('^(\d+)-(\d+)$')(m) -> m \group(1)`, or `| #Matches('^\s*$') -> ...` without
it. The pattern is read once, when it is given, and a pattern that is wrong crashes with what
[`Regex.parse`](#regexparse) says of it. For a regex made at run time, use [`#MatchesRegex`](#matchesregex).

## `#MatchesRegex`

```nanyx fragment
pattern Regex -> string -> #MatchesRegex(Match) | _
```

Matches a string that `regex` matches anywhere in, with the [`Match`](#match) as its payload:
`| #MatchesRegex(digits)(m) -> m.text`, or `| #MatchesRegex(digits) -> ...` without it.

<!-- End of the generated reference. -->
