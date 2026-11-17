---
title: Regular Expressions
description: Finding, extracting and replacing text with nanyx.regex
order: 3
---

The compiler ships a package of regular expressions, `nanyx.regex`. Its engine takes time in proportion to the text
for every pattern, so a pattern from a user can't hang a program, and it has no backreferences or lookaround. Its
reference is the [regex](../stdlib/regex) page.

# Setup

```bash
nanyx new regexes
cd regexes
nanyx install nanyx.regex
```

A script names it in a `::dependency` line instead:

```nanyx fragment
::dependency nanyx.regex
```

Write a pattern as a raw, single-quoted string, `'\d+'`, so its backslashes need no escaping. `Regex.parse` gives back
`#some(Regex)`, or `#error(RegexError)` for a pattern that is wrong. A pattern written in the program is right or not
for good, so `orCrash` is the usual way to take it.

# Testing a whole string

Anchor the pattern with `^` and `$` to test the whole string rather than finding a part of it:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

def postcode = Regex.parse('^[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}$') \orCrash("bad pattern")

for ["SW1A 1AA", "M1 1AE", "nope"] | text ->
  println("{text}: {text \Regex.isMatch(postcode)}")

-- SW1A 1AA: true
-- M1 1AE: true
-- nope: false
```

# Taking a string apart with groups

Each `( )` is a group, numbered from 1, and `(?<name> )` names one. `Match.group` and `Match.named` give what a group
matched, as `#some(string)`, or `#none` when it took no part in the match:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

def date = Regex.parse('(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})') \orCrash("bad pattern")

match "Released on 2026-11-17." \Regex.find(date)
  | #some(m) ->
    println("year {m \named("year") \someOr("")}, month {m \named("month") \someOr("")}")
    println("found at {m.start}: {m.text}")
  | #notFound -> println("no date")

-- year 2026, month 11
-- found at 12: 2026-11-17
```

# Finding every match

`Regex.findAll` gives a sequence that finds each match only when it is pulled, so `seq.take` stops it early:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

def word = Regex.parse('\w+') \orCrash("bad pattern")

def words = "the quick brown fox" \Regex.findAll(word) \seq.map { m -> m.text }

println(words \seq.toList)
println(words \seq.take(2) \seq.toList)

-- [the, quick, brown, fox]
-- [the, quick]
```

# Replacing

`Regex.replace` replaces each match with a string in which `$1` is what group 1 matched and `${name}` what the group
named `name` did. `Regex.replaceWith` takes a function of the match instead:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

def date = Regex.parse('(\d{4})-(\d{2})-(\d{2})') \orCrash("bad pattern")
def spaces = Regex.parse('\s+') \orCrash("bad pattern")
def number = Regex.parse('\d+') \orCrash("bad pattern")

println("2026-11-17" \Regex.replace(date, with = '$3/$2/$1'))
println("too    many   spaces" \Regex.replace(spaces, with = " "))
println("a1b22c333" \Regex.replaceWith(number, { m -> "<{m.text}>" }))

-- 17/11/2026
-- too many spaces
-- a<1>b<22>c<333>
```

# Splitting

`Regex.split` splits a string where a pattern matches, which a fixed separator can't do:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

def separators = Regex.parse('\s*[,;]\s*') \orCrash("bad pattern")

println("red, green ;blue;  yellow" \Regex.split(separators))

-- [red, green, blue, yellow]
```

# A pattern built at run time

A pattern made from input can be wrong, and the `RegexError` says where, counting code points from 1:

```nanyx
::dependency nanyx.regex

import nanyx.regex/regex

for ['[a-z]+', '(open', 'a{3,1}'] | pattern ->
  match Regex.parse(pattern)
    | #some(_) -> println("{pattern}: fine")
    | #error(e) -> println("{pattern}: column {e.column}: {e.message}")

-- [a-z]+: fine
-- (open: column 1: There is no ')' to close this '('
-- a{3,1}: column 2: The least count, 3, is more than the most, 1
```
