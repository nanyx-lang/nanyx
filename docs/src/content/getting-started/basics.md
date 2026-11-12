---
title: "Basics"
description: "Core syntax and expressions"
order: 4
---

This page covers core syntax and expression rules to help you read and write simple Nanyx code.

# Expressions

Everything is an expression that produces a value.

```nanyx
def message = "Hello world"
```

# Definitions

Use `def` to bind a name to a value. The value can be any expression, including another definition or a function:

```nanyx
def name = "Nanyx"
def version = 4
def getMessage = { name -> "Hello, {name}!" }
```

# Blocks and indentation

Blocks use indentation. A more-indented line belongs to the previous line. If a line ends with `->`, the next indented block is part of the expression.

```nanyx
def message =
  def preamble = "Hello"
  def audience = "World"

  "{preamble} {audience}"
```

You can also continue an expression on the next line when the previous line is incomplete:

```nanyx
def message = "Hello " +
  "World"
```

A block returns the value of its last expression:

```nanyx
def answer =
  def x = 20
  def y = 22
  x + y
```

For more on expression forms, see [Functions](../tour/functions) and [Pattern matching](../tour/pattern-matching).

# Numbers

Nanyx includes familiar numeric literals:

```nanyx
def count = 42
def ratio = 3.14159
def million = 1_000_000
```

An underscore groups digits, in an int or a float (`3.141_592`), and has no other meaning. It goes between two digits:
`1__000` and `1_` are errors.

You can use standard arithmetic and comparison operators:

```nanyx
def total = 10 + 5 * 2
def bigger = total > 12
```

For operator behavior, see [Operators](../tour/operators). For range syntax, see [Ranges](../tour/ranges).

# Strings

There are two kinds of string literal. Double-quoted strings support interpolation with `{...}`:

```nanyx
def name = "Alex"
def greeting = "Hello, {name}!"
```

A hole can hold any value whose type has a [`ToString`](../advanced/contexts#tostringa): numbers, booleans, strings and tags have one already, so do tuples, records and lists of values that have one, and a type of your own gets one by attaching a `toString`:

```nanyx
type Point = (x: int, y: int)
def Point.toString = { p -> "({p.x}, {p.y})" }

def total = 3
def where = "{total} items at {Point(x = 1, y = 2)}"   // "3 items at (1, 2)"
```

A function has no `ToString`, so putting one in a hole is an error, as is a list or a record that holds one.

Double-quoted strings have backslash escapes:

| Escape | Stands for |
| --- | --- |
| `\n` | a line break |
| `\t` | a tab |
| `\r` | a carriage return |
| `\\` | a backslash |
| `\"` | a double quote |
| `\{` and `\}` | a brace, which does not start a hole |
| `\u{...}` | the character with that code point, in hex: `\u{e9}` is `é` |

```nanyx
def lines = "first\nsecond"
def quoted = "say \"hi\""
def template = "Hello, {name}! Braces look like \{this}."
```

Any other character after a backslash is an error. A double-quoted string can also run across lines, and a line break in it is part of the string.

Single-quoted strings are _raw_: nothing inside them is interpolated or escaped, so braces, backslashes and quotes are taken literally. Use them for JSON, regular expressions, templates, and anything else that contains braces or backslashes of its own:

```nanyx
def json = '{"name": "Alice", "age": 30}'
def emailPattern = '[^@]+@\w+\.\w+'
```

For library string operations (`length`, `split`, `trim`, etc.), see [string stdlib](../stdlib/string).

# Equality and comparison

Use `==` and `!=` for equality and inequality:

```nanyx
def same = 10 == 10
def different = "a" != "b"
```

Use `<`, `<=`, `>`, `>=` for ordering comparisons:

```nanyx
def isAdult = 21 >= 18
```

Boolean values are combined with the keywords `and`, `or` and `not`:

```nanyx
def canVote = isAdult and isRegistered
def needsHelp = not isAdult or isNew
```

These operators are part of the language's operator system; see [Operators](../tour/operators) for details.

# Mutability

Mutability is available in Nanyx, but is considered a somewhat advanced feature. Read about [the Memory context](../advanced/memory) for more.

For immutable record updates, see [Copy and update (non-destructive mutation)](../tour/records#copy-and-update-non-destructive-mutation).

# Commas and newlines

You can use commas or newlines to separate items in lists and records:

```nanyx
def items = [1, 2, 3]

def items = [
  1
  2
  3
]

def myRecord = (a = 1, b = 2, c = 3)

def myRecord = (
  a = 1
  b = 2
  c = 3
)
```

# Comments

Single-line comments use `--`. Multi-line comments start and end with a line containing only three or more `---`.

```nanyx
-- This is a single-line comment

---
This is a multi-line comment.
---

----------------------------------
This is also a multi-line comment.
----------------------------------
```

Multi-line comments must be closed with the same number of dashes that they were opened with. A line of dashes with
text after it, `--- like this`, is a single-line comment.

## Doc comments

A doc comment starts with `--|`. It documents what comes on the line after it, and goes on for every line indented
further than the line the `--|` is on, so its other lines need no `--`:

```nanyx
--| The `length` bytes of `s` starting at byte `start`. Out-of-range values are clamped, so the result
  is never longer than what `s` actually holds.
export def string.substring: (string, start: int, length: int) -> string = { s, start, length -> ... }
```

The text is Markdown. A blank line stays in the comment while the text after it is still indented, so a doc comment
can have paragraphs and code examples. A name in backticks and square brackets, like `` [`string.slice`] ``, links to that
definition: the editor shows the link when you hover over the name the comment documents.

```nanyx
--| Adds one to `n`.

  For example:

      inc(1) == 2

  See also [`dec`].
def inc = { n -> n + 1 }
```

A doc comment can document a `def` (at the top level or inside a function), a `type` or `context`, a member of a
context, a field of a record type and a tag of a union type:

```nanyx
--| A shape on the screen.
type Shape =
  --| A circle, by its radius.
  | #circle(float)
  --| A square, by the length of its side.
  | #square(float)

type Point = (
  --| Pixels from the left.
  x: int
  --| Pixels from the top.
  y: int
)
```

A doc comment at the top of a file, before `module` or the first `import`, documents the module, and the editor shows
it wherever the module is named: in its `module` line, in an `import` of it and in an alias that qualifies a name.

```nanyx
--| Fizzbuzzbaz is a variant of fizzbuzz where the rules are supplied at
  runtime.
module fizzbuzzbaz
```

Anywhere else, a
doc comment that isn't followed by something it can document gets a warning from the
[official linter](../standard-tools/official-linter#doc-comment-that-documents-nothing). A `---` block comment is
always an ordinary comment, so commenting code out never turns it into documentation.

## Tags, ranges and records

Nanyx also supports tags, ranges, and records as core value forms. See [Tags](../tour/tags), [Ranges](../tour/ranges), and [Records and tuples](../tour/records).

# Table literals

Table literals are a shorthand for lists of records with the same shape:

```nanyx
def namesAndAges =
  [| name     | age |
   | "Alex"   | 22  |
   | "Taylor" | 30  |
   | "Sam"    | 28  |
   | "Morgan" | 45  |]
```

# Conditionals

`if` is an expression with a `then` branch and an `else` branch:

```nanyx
def label = if count == 0 then "empty" else "has items"

if cond then
  someCode()
else
  someOtherCode()
```

When there are more than two cases, use the multi-branch form. This is sugar for a `match` where each arm is a boolean condition, tested in order, with `else` as the final catch-all:

```nanyx
if | cond1 ->
     someCode()
   | cond2 ->
     someOtherCode()
   | else ->
     yetMoreCode()
```

As with a single-branch `if`, the `else` arm may be left out when the branches are unit; if no condition holds, the `if` is `()`. An arm's condition may also be a pattern test with `is` (`| get(3) is #some(v) -> ...`), which binds the pattern's names in that arm.

`if` can also test a value against a pattern with `is`, as in `if get(3) is #some(v) then println("got {v}")`; see [Pattern matching](../tour/pattern-matching#if-matching).

# Unimplemented code

Use `???` as a placeholder to keep code compiling while you iterate:

```nanyx
def myFunction = { () -> ??? }
```

`???` fits wherever a value is expected, whatever its type, and its type is written `???` too: `myFunction` is a `() -> ???`. The compiler reports it as an error (`Not implemented`), but like a type error it does not stop a debug build: `nanyx run` runs the program, and if it reaches the `???`, the program stops there and says so. A release build (`--release`) refuses it, so unfinished code is not shipped by accident.
