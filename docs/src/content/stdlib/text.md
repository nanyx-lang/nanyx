---
title: text
description: Functions on strings
order: 10
---

Import it as `nanyx/text`. `string.length` is also a built-in property of strings and lists, so `s.length` is the string's length, as `string.length(s)` is. `string.toString`, which gives back the string itself, is in the prelude rather than in this module, with the `toString` of the other built-in types: see [Converting to text](prelude-contexts#converting-to-text).

```nanyx
import nanyx/console
import nanyx/text

println("a,b,c" \split(on = ","))
println("7" \padStart(width = 3, fill = "0"))
println(["Hello", "World"] \string.join(", "))
println("héllo" \chars)
-- [a, b, c]
-- 007
-- Hello, World
-- [h, é, l, l, o]
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

Functions on strings.

Each is attached to `string`, so with `import nanyx/text` it is called as `string.trim(s)`, or as
`s \trim` when `s` is known to be a string. A dot on a value never reaches an attached definition:
`s.trim` is an error.

Strings are UTF-8. Most of these functions work byte by byte: `length` counts bytes (not
characters or grapheme clusters), indices and widths are byte offsets, `uppercase` and `lowercase`
only change ASCII letters, and the trimming and `words` functions only treat ASCII whitespace as
whitespace. Multi-byte characters pass through unchanged, except that `padStart` and `padEnd` cut
their fill at a byte count, so a multi-byte `fill` can be cut mid-character. `chars`, `reverse` and
`codePoints` work by character, where a character is one Unicode code point: they read each UTF-8
sequence's lead byte, so they never cut a character in half. `fromCodePoint` turns a code point
back into a character.

Every function here is stack-safe: each one that walks a string calls itself only in tail
position, so it works on a string of any length.

## `string.length`

```nanyx fragment
def string.length: string -> int
```

The number of bytes in `s`.

## `string.substring`

```nanyx fragment
def string.substring: (string, length: int, start: int) -> string
```

The `length` bytes of `s` starting at byte `start`. Out-of-range values are clamped, so the result
is never longer than what `s` actually holds.

## `string.take`

```nanyx fragment
def string.take: string, int -> string
```

The first `n` bytes of `s`, or all of `s` when it is shorter. A negative `n` takes none. Bytes are
counted as `substring` and `indexOf` count them, so `s \take(i)` cuts at an index `indexOf` gave.

## `string.skip`

```nanyx fragment
def string.skip: string, int -> string
```

All but the first `n` bytes of `s`, or `""` when it is shorter. A negative `n` skips none.

## `string.startsWith`

```nanyx fragment
def string.startsWith: string, string -> bool
```

Whether `s` begins with `prefix`.

## `string.endsWith`

```nanyx fragment
def string.endsWith: string, string -> bool
```

Whether `s` ends with `suffix`.

## `string.indexOf`

```nanyx fragment
def string.indexOf: string, string -> #some(int) | #notFound
```

The byte index of the first occurrence of `needle` in `s`. An empty `needle` is found at 0.

## `string.lastIndexOf`

```nanyx fragment
def string.lastIndexOf: string, string -> #some(int) | #notFound
```

The byte index of the last occurrence of `needle` in `s`. An empty `needle` is found at the end.

## `string.contains`

```nanyx fragment
def string.contains: string, string -> bool
```

Whether `needle` occurs anywhere in `s`.

## `string.count`

```nanyx fragment
def string.count: string, string -> int
```

The number of non-overlapping occurrences of `needle` in `s`. An empty `needle` is counted 0 times.

## `string.removePrefix`

```nanyx fragment
def string.removePrefix: string, string -> string
```

`s` without `prefix`, or `s` unchanged when it doesn't start with `prefix`.

## `string.removeSuffix`

```nanyx fragment
def string.removeSuffix: string, string -> string
```

`s` without `suffix`, or `s` unchanged when it doesn't end with `suffix`.

## `string.uppercase`

```nanyx fragment
def string.uppercase: string -> string
```

`s` with every ASCII letter upper-cased.

## `string.lowercase`

```nanyx fragment
def string.lowercase: string -> string
```

`s` with every ASCII letter lower-cased.

## `string.trim`

```nanyx fragment
def string.trim: string -> string
```

`s` without leading and trailing whitespace.

## `string.trimStart`

```nanyx fragment
def string.trimStart: string -> string
```

`s` without leading whitespace.

## `string.trimEnd`

```nanyx fragment
def string.trimEnd: string -> string
```

`s` without trailing whitespace.

## `string.isEmpty`

```nanyx fragment
def string.isEmpty: string -> bool
```

Whether `s` has no bytes at all.

## `string.isBlank`

```nanyx fragment
def string.isBlank: string -> bool
```

Whether `s` is empty or only whitespace.

## `string.repeat`

```nanyx fragment
def string.repeat: string, int -> string
```

`s` repeated `times` times (the empty string when `times` is zero or negative). Stack-safe: it works
for any `times`.

## `string.padStart`

```nanyx fragment
def string.padStart: (string, fill: string, width: int) -> string
```

`s` with `fill` repeated in front of it until it is `width` bytes long. Longer strings are unchanged.

## `string.padEnd`

```nanyx fragment
def string.padEnd: (string, fill: string, width: int) -> string
```

`s` with `fill` repeated after it until it is `width` bytes long. Longer strings are unchanged.

## `string.split`

```nanyx fragment
def string.split: (string, on: string) -> list(string)
```

The pieces of `s` between occurrences of `on`. An empty `on` yields `[s]`. Stack-safe:
it works however many pieces there are.

## `string.splitOnce`

```nanyx fragment
def string.splitOnce: (string, on: string) -> #some(string, string) | #notFound
```

The part of `s` before the first `on`, and the part after it, or `#notFound` when `s` does not
contain `on`. An empty `on` is found at the start, so it gives `("", s)`.

## `string.join`

```nanyx fragment
def string.join: list(string), string -> string
```

The `pieces` concatenated with `separator` between each pair.

## `string.concat`

```nanyx fragment
def string.concat: list(string) -> string
```

The `pieces` concatenated end to end.

## `string.replace`

```nanyx fragment
def string.replace: (string, pattern: string, replacement: string) -> string
```

`s` with every occurrence of `pattern` replaced by `replacement`. An empty `pattern` leaves `s` unchanged.

## `string.lines`

```nanyx fragment
def string.lines: string -> list(string)
```

The lines of `s`, split on line feeds, with any carriage return before a line feed dropped. A final
line break doesn't start another line, so "a\nb\n" has two lines and "" has none. Stack-safe: it
works however many lines there are.

## `string.words`

```nanyx fragment
def string.words: string -> list(string)
```

The runs of non-whitespace in `s`, in order. Stack-safe: it works however many words there are.

## `string.chars`

```nanyx fragment
def string.chars: string -> list(string)
```

Each character of `s` as a string of its own, in order. A character is one Unicode code point, so
"héllo" has five characters though it is six bytes long. Stack-safe: it works on a string of any
length.

## `string.reverse`

```nanyx fragment
def string.reverse: string -> string
```

`s` with its characters (code points, not bytes) in reverse order, so it never cuts a character in
half. Stack-safe: it works on a string of any length.

## `string.codePoints`

```nanyx fragment
def string.codePoints: string -> list(int)
```

The Unicode code point of each character of `s`, in order: `"aé€" \codePoints` is
`[97, 233, 8364]`. It has one number for each of [`string.chars`](#stringchars). Stack-safe: it works on a string
of any length.

## `string.fromCodePoint`

```nanyx fragment
def string.fromCodePoint: int -> #some(string) | #invalidCodePoint
```

The character whose Unicode code point is `codePoint`, as a string: `string.fromCodePoint(233)` is
`#some("é")`. A negative number, a surrogate (55296 to 57343, `0xD800` to `0xDFFF`) or a number past
the last code point (1114111, `0x10FFFF`) gives `#invalidCodePoint`.

<!-- End of the generated reference. -->
