---
title: "string"
description: "String manipulation functions"
order: 6
---

The `string` module provides functions for working with UTF-8 strings. Import it as `nanyx/text`.

These functions are [attached definitions](../advanced/attached-definitions) on `string`, so when the receiver is known to be a string they can be piped into without qualification: `s \split(on = ",")`, `s \startsWith("nan")`. They are called as `string.trim(s)` or `s \trim`; a dot on a value never reaches an attached definition, so `s.trim` is an error. `length` is the exception: it is also a built-in property of strings and lists, so `s.length` is the string's length, as `string.length(s)` is.

For now most of these functions work byte by byte. `length` counts bytes, indices and widths are byte offsets, `uppercase` and `lowercase` change only ASCII letters, and the trimming functions and `words` treat only ASCII whitespace as whitespace. Characters outside ASCII pass through unchanged, except that `padStart` and `padEnd` cut their fill at a byte count, so a `fill` of such characters can be cut in the middle of one.

[`chars`](#chars) and [`reverse`](#reverse) work by character instead. A character here is one Unicode code point, so `"é"` is one character even though it takes two bytes. A letter with a combining accent (a grapheme cluster of two code points) counts as two.

Every one of them is stack-safe: `repeat`, `split`, `replace`, `lines`, `words`, `chars`, `reverse` and the rest work on a string of any length, and give any number of pieces, without running out of stack. Each walks the string with [tail calls](../tour/functions#tail-recursion).

`string.toString`, which gives back the string itself, is in the prelude rather than in this module, with the `toString` of the other built-in types: see [Converting to text](./standard-library#converting-to-text).

# length

```nanyx
string.length: string -> int
```

Returns the number of bytes in the string's UTF-8 encoding. A character outside ASCII takes more than one byte, so it counts as more than one.

```nanyx
string.length("hello")
-- 5

string.length("héllo")
-- 6
```

---

# uppercase

```nanyx
string.uppercase: string -> string
```

```nanyx
string.uppercase("hello")
-- "HELLO"
```

---

# lowercase

```nanyx
string.lowercase: string -> string
```

```nanyx
string.lowercase("HELLO")
-- "hello"
```

---

# split

```nanyx
string.split: (string, on: string) -> list(string)
```

```nanyx
string.split("a,b,c", on = ",")
-- ["a", "b", "c"]
```

---

# splitOnce

```nanyx
string.splitOnce: (string, on: string) -> #some(string, string) | #notFound
```

The part of the string before the first `on`, and the part after it, which may contain `on` again. `#notFound` when the string does not contain `on`; an empty `on` is found at the start.

```nanyx
string.splitOnce("key=a=b", on = "=")
-- #some("key", "a=b")
```

---

# contains

```nanyx
string.contains: (string, string) -> bool
```

```nanyx
string.contains("hello world", "world")
-- true
```

---

# startsWith

```nanyx
string.startsWith: (string, string) -> bool
```

```nanyx
string.startsWith("nanyx", "nan")
-- true
```

---

# endsWith

```nanyx
string.endsWith: (string, string) -> bool
```

```nanyx
string.endsWith("nanyx", "nyx")
-- true
```

---

# indexOf

```nanyx
string.indexOf: (string, string) -> #some(int) | #notFound
```

```nanyx
string.indexOf("hello", "ll")
-- #some(2)
```

---

# replace

```nanyx
string.replace: (string, pattern: string, replacement: string) -> string
```

```nanyx
string.replace("hello world", pattern = "world", replacement = "Nanyx")
-- "hello Nanyx"
```

---

# substring

```nanyx
string.substring: (string, start: int, length: int) -> string
```

```nanyx
string.substring("nanyx", start = 1, length = 3)
-- "any"
```

---

# take and skip

```nanyx
string.take: string, int -> string
string.skip: string, int -> string
```

`take` is the first `n` bytes, or the whole string when it is shorter. `skip` is all but the first `n` bytes, or `""`
when it is shorter. A negative count takes or skips none. They count bytes as `substring` and `indexOf` do, so they
cut at an index `indexOf` returned.

```nanyx
"hello" \take(2)   -- "he"
"hello" \skip(2)   -- "llo"
"hello" \take(10)  -- "hello"
"hello" \skip(10)  -- ""
```

---

# trim

```nanyx
string.trim: string -> string
```

Removes leading and trailing whitespace.

```nanyx
string.trim("  hello  ")
-- "hello"
```

---

# trimStart, trimEnd

```nanyx
string.trimStart: string -> string
string.trimEnd: string -> string
```

Remove only the leading, or only the trailing, whitespace.

---

# lastIndexOf

```nanyx
string.lastIndexOf: (string, string) -> #some(int) | #notFound
```

```nanyx
string.lastIndexOf("abcabc", "bc")
-- #some(4)
```

---

# count

```nanyx
string.count: (string, string) -> int
```

The number of non-overlapping occurrences.

```nanyx
string.count("banana", "an")
-- 2
```

---

# removePrefix, removeSuffix

```nanyx
string.removePrefix: (string, string) -> string
string.removeSuffix: (string, string) -> string
```

The string without the prefix (or suffix), or unchanged when it doesn't have it.

```nanyx
string.removePrefix("nanyx", "nan")
-- "yx"
```

---

# isEmpty, isBlank

```nanyx
string.isEmpty: string -> bool
string.isBlank: string -> bool
```

`isBlank` is also true for a string of only whitespace.

---

# repeat

```nanyx
string.repeat: (string, int) -> string
```

```nanyx
string.repeat("ab", 3)
-- "ababab"
```

---

# padStart, padEnd

```nanyx
string.padStart: (string, width: int, fill: string) -> string
string.padEnd: (string, width: int, fill: string) -> string
```

Repeat `fill` in front of (or after) the string until it is `width` long. A longer string is unchanged.

```nanyx
string.padStart("7", width = 3, fill = "0")
-- "007"
```

---

# lines, words

```nanyx
string.lines: string -> list(string)
string.words: string -> list(string)
```

`lines` splits on line breaks (a final one doesn't start another line); `words` gives the runs of non-whitespace.

```nanyx
string.words("  one two   three ")
-- ["one", "two", "three"]
```

---

# chars

```nanyx
string.chars: string -> list(string)
```

Each character of the string as a string of its own, in order. A character is one code point, so a string can have fewer characters than bytes, and `s \chars \string.concat` is `s` again.

```nanyx
"héllo" \chars
-- ["h", "é", "l", "l", "o"]

list.length(string.chars("héllo"))
-- 5

string.chars("")
-- []
```

---

# reverse

```nanyx
string.reverse: string -> string
```

The string with its characters, not its bytes, in reverse order, so a character outside ASCII is never cut in half.

```nanyx
string.reverse("héllo")
-- "olléh"
```

---

# concat

```nanyx
string.concat: list(string) -> string
```

```nanyx
string.concat(["Hello", ", ", "World"])
-- "Hello, World"
```

---

# join

```nanyx
string.join: (list(string), string) -> string
```

```nanyx
string.join(["a", "b", "c"], "-")
-- "a-b-c"
```
