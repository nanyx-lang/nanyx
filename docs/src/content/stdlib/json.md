---
title: json
description: Reading and writing JSON
order: 15.5
package: nanyx.json 0.5.1
---

`nanyx.json` isn't part of the standard library: it is a package published to the
[package index](https://github.com/nanyx-lang/packages) from [nanyx-lang/json](https://github.com/nanyx-lang/json). An
application lists it in its manifest's `dependencies`, `(package = "nanyx.json", version = "0.5")`, which
`nanyx install nanyx.json` adds, and a script names the version it uses in a `::install nanyx.json 0.5.1` line. Import
`nanyx.json/json` under a name, `as json`, as these examples do: its `print` would otherwise clash with the console's.
It is pure, so it needs no context.

To write a value of your own type as JSON, and read it back, `json.encode(value)` and
`json.decode(type T, text)` walk the type, with nothing written for it: see [reflection](json-reflection). To
write or read one another way, build a [decoder](json-typed) or an [encoder](encode). This page is the `Json` value
underneath them, which `json.parse` reads and `json.print` writes:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

def text = '{"name": "Ada", "born": 1815, "languages": ["English", "French"]}'

match json.parse(text)
  | #some(value) -> println(json.print(value))
  | #invalidJson(e) -> println("Not JSON: {e.message}")

-- {"name":"Ada","born":1815,"languages":["English","French"]}
```

A `Json` value is a tag union, so it is built and taken apart as any other is: an object is a `list((string, Json))`,
its members in the order they are written, and an array a `list(Json)`.

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

def point = #object([("x", #number(1.5)), ("y", #number(-2.0))])

println(json.print(point))

-- {"x":1.5,"y":-2}
```

An object keeps its members in the order they are written: `parse` reads them so, and `print` writes them so.
`json.member(members, key)` looks one up, giving `#some` of its value or `#notFound`. Two objects are `==` when they have
the same members in the same order.

When the text isn't JSON, `parse` gives `#invalidJson`, which says where, by byte and by line and column, and what it
found there. `json.describe` writes it as a message:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

match json.parse("[1, 2")
  | #some(_) -> println("JSON")
  | #invalidJson(e) -> println("{e.line}:{e.column}: {e.message}")

match json.parse('{"a" 1}')
  | #some(_) -> println("JSON")
  | failure -> println(json.describe(failure))

-- 1:6: Expected ',' or ']' after an element of an array, found the end of the text
-- Expected ':' after the name of a member, found '1', at line 1, column 6
```

# Reference

<!-- The reference below is generated from the doc comments of nanyx.json 0.5.1, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->

JSON text, read into a [`Json`](#json) value and written back out.

[`parse`](#parse) reads text into a `Json`, or says where it stops being JSON and why, as a
[`JsonError`](#jsonerror). [`print`](#print) writes a value compactly, and [`printPretty`](#printpretty) writes it with each
element and member on a line of its own.

[`encode`](json-reflection#encode) writes a value of any type as JSON text, and [`decode`](json-reflection#decode) reads one back, by walking
the type, with no encoder or decoder written for it: `json.decode(type User, text)`. To
write or read a type another way, build an `Encoder` or a `Decoder` from smaller ones, and
run it with [`encodeWith`](json-typed#encodewith) or [`decodeWith`](json-typed#decodewith). A program names
`nanyx.json` in its manifest's `dependencies`, or a script in an `::install` line, and imports
it as `import nanyx.json/json as json`, to call `json.parse(text)` as the docs do.

An object is a list of its members, each a key and a value, in the order they are written, so
[`parse`](#parse) and [`print`](#print) keep the order of an object's keys, and [`encode`](json-reflection#encode) writes a record's
fields in the order its type declares them. A key written twice in the text is read once, where
it is first written, with the value written last, as `{"a": 1, "b": 2, "a": 3}` is
`{"a": 3, "b": 2}`. Two objects are `==` when they have the same members in the same order. A
`map` of values is written in the map's order, which is its keys' hashes'.

Text is UTF-8, as strings are, and a string's escapes are read as JSON writes them: `\"`, `\\`,
`\/`, `\b`, `\f`, `\n`, `\r`, `\t` and `\uXXXX`, with a character past U+FFFF written as two
`\u` escapes, a surrogate pair. Numbers are `float`s.

Each function walks its text, or its value, once, and calls itself in tail position along an
array or an object, so a document of any length is read and written in time in proportion to
its size. Only nesting takes stack: an array in an array in an array, and so on.

## `Json`

```nanyx fragment
type Json = #null | #bool(bool) | #number(float) | #string(string) | #array(list(self)) | #object(list((string, self)))
```

A JSON value: `null`, a boolean, a number, a string, an array of values, or an object, whose
members are values each named by a string, in the order they are written.

## `JsonError`

```nanyx fragment
type JsonError = #invalidJson(position: int, line: int, column: int, message: string)
```

Why [`parse`](#parse) read no value: `#invalidJson`, with where the text stops being JSON and what it
found there. `position` is the byte the problem is at, counting from 0, and `line` and `column`
say where that byte is, each counting from 1, with `column` counting characters.

## `member`

```nanyx fragment
def member: list((string, Json)), string -> #some(Json) | #notFound
```

The value of the member `key` of an object's `members`, the first of that name, or `#notFound`:
`json.member(person, "name")` for the object `#object(person)`.

## `parse`

```nanyx fragment
def parse: string -> #some(Json) | #invalidJson(position: int, line: int, column: int, message: string)
```

The JSON value `text` writes, or an `#invalidJson` that says where it stops being JSON and why.
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
their order. What `print` writes, [`parse`](#parse) reads back as the same value.

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
