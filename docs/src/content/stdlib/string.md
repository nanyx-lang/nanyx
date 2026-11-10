---
title: "string"
description: "String manipulation functions"
order: 4
---

The `string` module provides functions for working with UTF-8 strings. Import it as `nanyx/text`.

These functions are [attached definitions](../advanced/attached-definitions) on `String`, so when the receiver is known to be a string they can be piped into without qualification: `s \split(on = ",")`, `s \startsWith("nan")`. A string's length is also a built-in property, read with a dot: `s.length`.

Every one of them is stack-safe: `repeat`, `split`, `replace`, `lines`, `words` and the rest work on a string of any length, and give any number of pieces, without running out of stack. Each walks the string with [tail calls](../tour/functions#tail-recursion).

`String.toString`, which gives back the string itself, is in the prelude rather than in this module, with the `toString` of the other built-in types: see [Converting to text](./standard-library#converting-to-text).

# length

```nanyx
String.length: string -> int
```

Returns the number of grapheme clusters in the string.

```nanyx
String.length("hello")
-- 5
```

---

# uppercase

```nanyx
String.uppercase: string -> string
```

```nanyx
String.uppercase("hello")
-- "HELLO"
```

---

# lowercase

```nanyx
String.lowercase: string -> string
```

```nanyx
String.lowercase("HELLO")
-- "hello"
```

---

# split

```nanyx
String.split: (string, on: string) -> list(string)
```

```nanyx
String.split("a,b,c", on = ",")
-- ["a", "b", "c"]
```

---

# contains

```nanyx
String.contains: (string, string) -> bool
```

```nanyx
String.contains("hello world", "world")
-- true
```

---

# startsWith

```nanyx
String.startsWith: (string, string) -> bool
```

```nanyx
String.startsWith("nanyx", "nan")
-- true
```

---

# endsWith

```nanyx
String.endsWith: (string, string) -> bool
```

```nanyx
String.endsWith("nanyx", "nyx")
-- true
```

---

# indexOf

```nanyx
String.indexOf: (string, string) -> #some(int) | #notFound
```

```nanyx
String.indexOf("hello", "ll")
-- #some(2)
```

---

# replace

```nanyx
String.replace: (string, pattern: string, replacement: string) -> string
```

```nanyx
String.replace("hello world", pattern = "world", replacement = "Nanyx")
-- "hello Nanyx"
```

---

# substring

```nanyx
String.substring: (string, start: int, length: int) -> string
```

```nanyx
String.substring("nanyx", start = 1, length = 3)
-- "any"
```

---

# trim

```nanyx
String.trim: string -> string
```

Removes leading and trailing whitespace.

```nanyx
String.trim("  hello  ")
-- "hello"
```

---

# trimStart, trimEnd

```nanyx
String.trimStart: string -> string
String.trimEnd: string -> string
```

Remove only the leading, or only the trailing, whitespace.

---

# lastIndexOf

```nanyx
String.lastIndexOf: (string, string) -> #some(int) | #notFound
```

```nanyx
String.lastIndexOf("abcabc", "bc")
-- #some(4)
```

---

# count

```nanyx
String.count: (string, string) -> int
```

The number of non-overlapping occurrences.

```nanyx
String.count("banana", "an")
-- 2
```

---

# removePrefix, removeSuffix

```nanyx
String.removePrefix: (string, string) -> string
String.removeSuffix: (string, string) -> string
```

The string without the prefix (or suffix), or unchanged when it doesn't have it.

```nanyx
String.removePrefix("nanyx", "nan")
-- "yx"
```

---

# isEmpty, isBlank

```nanyx
String.isEmpty: string -> bool
String.isBlank: string -> bool
```

`isBlank` is also true for a string of only whitespace.

---

# repeat

```nanyx
String.repeat: (string, int) -> string
```

```nanyx
String.repeat("ab", 3)
-- "ababab"
```

---

# padStart, padEnd

```nanyx
String.padStart: (string, width: int, fill: string) -> string
String.padEnd: (string, width: int, fill: string) -> string
```

Repeat `fill` in front of (or after) the string until it is `width` long. A longer string is unchanged.

```nanyx
String.padStart("7", width = 3, fill = "0")
-- "007"
```

---

# lines, words

```nanyx
String.lines: string -> list(string)
String.words: string -> list(string)
```

`lines` splits on line breaks (a final one doesn't start another line); `words` gives the runs of non-whitespace.

```nanyx
String.words("  one two\tthree ")
-- ["one", "two", "three"]
```

---

# concat

```nanyx
String.concat: list(string) -> string
```

```nanyx
String.concat(["Hello", ", ", "World"])
-- "Hello, World"
```

---

# join

```nanyx
String.join: (list(string), string) -> string
```

```nanyx
String.join(["a", "b", "c"], "-")
-- "a-b-c"
```
