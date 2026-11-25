---
title: "prelude: chars"
description: Converting a char to and from its code point and a string, and testing what it is
order: 5.5
---

The definitions attached to [`char`](../tour/types#characters), one Unicode scalar value. They are in the
[prelude](prelude), so no import is needed. Two chars are equal, ordered and hashed by their code points. A string's
chars are [`string.chars`](text#stringchars), and [`string.fromChars`](text#stringfromchars) makes a string of them.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `char.toInt`

```nanyx fragment
def char.toInt: char -> int
```

The Unicode code point of `c`: `char.toInt("A")` is `65`, and `char.toInt("é")` is `233`.

## `char.fromInt`

```nanyx fragment
def char.fromInt: int -> #some(char) | #invalidCodePoint
```

The char whose Unicode code point is `n`: `char.fromInt(233)` is `#some("é")`. A negative number, a
surrogate (55296 to 57343, `0xD800` to `0xDFFF`) or a number past the last code point (1114111,
`0x10FFFF`) gives `#invalidCodePoint`.

## `char.toString`

```nanyx fragment
def char.toString: char -> string
```

`c` as a string of its own: its UTF-8 encoding, 1 to 4 bytes. `char.toString("é")` is `"é"`.

## `char.default`

```nanyx fragment
def char.default: char
```

The char whose code point is 0, as Rust's `char` has.

## `char.isDigit`

```nanyx fragment
def char.isDigit: char -> bool
```

Whether `c` is an ASCII digit, `0` to `9`.

## `char.isAsciiLetter`

```nanyx fragment
def char.isAsciiLetter: char -> bool
```

Whether `c` is an ASCII letter, `a` to `z` or `A` to `Z`.

## `char.isAsciiUppercase`

```nanyx fragment
def char.isAsciiUppercase: char -> bool
```

Whether `c` is an ASCII upper-case letter, `A` to `Z`: `char.isAsciiUppercase("é")` is `false`.

## `char.isAsciiLowercase`

```nanyx fragment
def char.isAsciiLowercase: char -> bool
```

Whether `c` is an ASCII lower-case letter, `a` to `z`: `char.isAsciiLowercase("é")` is `false`.

## `char.isAsciiAlphanumeric`

```nanyx fragment
def char.isAsciiAlphanumeric: char -> bool
```

Whether `c` is an ASCII letter or digit, `a` to `z`, `A` to `Z` or `0` to `9`.

## `char.uppercase`

```nanyx fragment
def char.uppercase: char -> char
```

`c` upper-cased if it is an ASCII lower-case letter, else `c` as it is, as `string.uppercase` does
each char: `char.uppercase("q")` is `"Q"`, and `char.uppercase("é")` is `"é"`.

## `char.lowercase`

```nanyx fragment
def char.lowercase: char -> char
```

`c` lower-cased if it is an ASCII upper-case letter, else `c` as it is, as `string.lowercase` does
each char: `char.lowercase("Q")` is `"q"`, and `char.lowercase("É")` is `"É"`.

## `char.isWhitespace`

```nanyx fragment
def char.isWhitespace: char -> bool
```

Whether `c` is a space, a tab, a line feed or a carriage return, the whitespace the string
functions trim and split on.

<!-- End of the generated reference. -->
