---
title: json
description: Reading and writing JSON
order: 15.5
package: nanyx.json 0.1.0
---

`nanyx.json` isn't part of the standard library: it is a package published to the
[package index](https://github.com/nanyx-lang/packages) from [nanyx-lang/json](https://github.com/nanyx-lang/json). An
application lists it in its manifest's `dependencies`, `(package = "nanyx.json", version = "0.1")`, which
`nanyx install nanyx.json` adds, and a script names the version it uses in a `::dependency nanyx.json 0.1.0` line. Import
`nanyx.json/json` under a name, `as json`, as these examples do: its `print` would otherwise clash with the console's.
It is pure, so it needs no context.

```nanyx
::dependency nanyx.json 0.1.0

import nanyx.json/json as json

def text = '{"name": "Ada", "born": 1815, "languages": ["English", "French"]}'

match json.parse(text)
  | #some(value) -> println(json.print(value))
  | #error(e) -> println("Not JSON: {e.message}")

-- {"name":"Ada","born":1815,"languages":["English","French"]}
```

A `Json` value is a tag union, so it is built and taken apart as any other is: an object is a `map(string, Json)`, and
an array a `list(Json)`.

```nanyx
::dependency nanyx.json 0.1.0

import nanyx.json/json as json

def point = #object(["x" => #number(1.5), "y" => #number(-2.0)])

println(json.print(point))

-- {"x":1.5,"y":-2}
```

An object keeps its keys in the order they were written, since a map keeps the order its keys were first added, so a
document read and written again keeps its keys where they were. Two objects with the same members in another order
are still `==`.

When the text isn't JSON, `parse` says where, by byte and by line and column, and what it found there:

```nanyx
::dependency nanyx.json 0.1.0

import nanyx.json/json as json

match json.parse("[1, 2")
  | #some(_) -> println("JSON")
  | #error(e) -> println("{e.line}:{e.column}: {e.message}")

-- 1:6: Expected ',' or ']' after an element of an array, found the end of the text
```

To read a document into types of your own, and write them back, use the [decoders](json-typed) and
[encoders](encode).

# Reference

<!-- The reference below is generated from the doc comments of nanyx.json 0.1.0, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->

JSON text, read into a [`Json`](#json) value and written back out.

[`parse`](#parse) reads text into a `Json`, or says where it stops being JSON and why, as a
[`JsonError`](#jsonerror). [`print`](#print) writes a value compactly, and [`printPretty`](#printpretty) writes it with each
element and member on a line of its own. Nothing here needs a context. A program names
`nanyx.json` in its manifest's `dependencies`, or a script in a `::dependency` line, and imports
it as `import nanyx.json/json as json`, to call `json.parse(text)` as the docs do.

An object is a `map(string, Json)`, and a map keeps its keys in the order they were first added:
`parse` adds them in the order the text writes them, and `print` writes them in the map's order,
so a document read and written again keeps its keys where they were. A key written twice holds
the value written last, in the place it was first written.

Text is UTF-8, as strings are, and a string's escapes are read as JSON writes them: `\"`, `\\`,
`\/`, `\b`, `\f`, `\n`, `\r`, `\t` and `\uXXXX`, with a character past U+FFFF written as two
`\u` escapes, a surrogate pair. Numbers are `float`s.

Each function walks its text, or its value, once, and calls itself in tail position along an
array or an object, so a document of any length is read and written in time in proportion to
its size. Only nesting takes stack: an array in an array in an array, and so on.

## `Json`

```nanyx fragment
type Json = #null | #bool(bool) | #number(float) | #string(string) | #array(list(self)) | #object(map(string, self))
```

A JSON value: `null`, a boolean, a number, a string, an array of values, or an object, whose
members are values each named by a string.

## `JsonError`

```nanyx fragment
type JsonError = (position: int, line: int, column: int, message: string)
```

Why [`parse`](#parse) read no value: where the text stops being JSON, and what it found there.
`position` is the byte the problem is at, counting from 0, and `line` and `column` say where
that byte is, each counting from 1, with `column` counting characters.

## `parse`

```nanyx fragment
def parse: string -> #some(Json) | #error(JsonError)
```

The JSON value `text` writes, or a [`JsonError`](#jsonerror) that says where it stops being JSON and why.
The text is one value, with any whitespace before and after it: `json.parse('[1, "two", null]')`
is `#some(#array([#number(1.0), #string("two"), #null]))`, and `json.parse("[1, 2")` is an error
at the end of the text.

## `print`

```nanyx fragment
def print: Json -> string
```

`value` as JSON text, written compactly, with no whitespace: the array of `#number(1.0)` and
`#string("two")` is `[1,"two"]`. A number with no fraction is written without one, as `1`, and
NaN and infinity, which JSON has no way to write, as `null`. An object's members are written in
its map's order. What `print` writes, [`parse`](#parse) reads back as the same value.

## `printPretty`

```nanyx fragment
def printPretty: (Json, indent: int) -> string
```

`value` as JSON text, written to be read by people: each element of an array and member of an
object on a line of its own, indented `indent` spaces further than the line the array or object
starts on, with a space after each member's `:`. An empty array or object is written `[]` or
`{}`. Numbers and strings are written as [`print`](#print) writes them, and [`parse`](#parse) reads what it
writes back as the same value.

<!-- End of the generated reference. -->
