---
title: json, decoders
description: Reading JSON into types of your own, and writing them back
order: 15.6
package: nanyx.json 0.5.1
---

`json.decode(type T, text)` reads most types with nothing written for them, by walking the type: see
[reflection](json-reflection). A decoder is for a document whose shape isn't the type's: under other keys, nested
differently, or with a tag in a member of its own. A `Decoder(a)` reads an `a` out of a `Json` value, and says where a
document doesn't fit. The decoders are in `nanyx.json/json` with the rest of [json](json), and the encoders, which go the
other way, in [encode](encode).

Some decoders read one kind of value each: `string`, `int` (a whole number), `float` and `bool`, and `value` reads any
value as it is. `json.decoderFor(type T)` reads a `T` by walking its type, as `json.decode` does. The rest build larger
decoders from smaller: `list`, `dict`, `optional`, `field`, `oneOf`, `convert`, `andThen`, and `record`, a builder
that reads a record a member at a time, each with `try`, and gives back what it read with `return`. `json.decodeWith`
parses text and runs a decoder on it, and `json.decodeValueWith` runs one on a value already parsed.

When a value doesn't fit, the decoder gives `#doesNotFit`, a `DecodeError`, which says where, by the path from the
document's root to it, which `json.pathText` writes as `users[2].age`, and what was wrong there; `json.describe` writes
both. Text that isn't JSON is an `#invalidJson`, as `json.parse` gives. A decoder takes time in proportion to the size
of what it reads, and reads a long list in constant stack.

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

type User = (name: string, age: int)

-- the name is nested in a member of its own, {"profile": {"name": ...}}
def user: json.Decoder(User) = json.record {
  def name = try json.field("profile", json.field("name", json.string))
  def age = try json.field("age", json.int)
  return User(name = name, age = age)
}

def show = { text ->
  match json.decodeWith(text, json.list(user))
    | #some(users) -> println(json.encode(users) \orCrash("users are written"))
    | failure -> println(json.describe(failure))
}

show('[{"profile": {"name": "Ada"}, "age": 36}]')
show('[{"profile": {"name": "Ada"}, "age": 36}, {"profile": {"name": "Bob"}, "age": 2.5}]')

-- [{"name":"Ada","age":36}]
-- [1].age: Expected a whole number, found 2.5
```

A tagged union whose tag is a member of its own is read by the decoder that member chooses: a `try` reads by any
decoder, including one picked by what an earlier `try` read.

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

type Shape = #circle(float) | #square(float)

def shape: json.Decoder(Shape) = json.record {
  def kind = try json.field("kind", json.string)

  def size: json.Decoder(Shape) =
    match kind
      | "circle" -> json.convert(json.field("radius", json.float), { r -> #circle(r) })
      | "square" -> json.convert(json.field("side", json.float), { s -> #square(s) })
      | other -> json.fail("Expected the kind 'circle' or 'square', found '{other}'")

  def s = try size
  return s
}

match json.decodeWith('{"kind": "square", "side": 2}', shape)
  | #some(#square(side)) -> println("A square of side {side}")
  | #some(_) -> println("Another shape")
  | failure -> println(json.describe(failure))

-- A square of side 2.0
```

# Reference

<!-- The reference below is generated from the doc comments of nanyx.json 0.5.1, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->

## `PathStep`

```nanyx fragment
type PathStep = #field(string) | #index(int)
```

One step on the way from a document's root to a value in it: the member of an object with this
name, or the element of an array at this index, counting from 0.

## `DecodeError`

```nanyx fragment
type DecodeError = #doesNotFit(path: list(PathStep), message: string)
```

Why a decoder read no value: `#doesNotFit`, with the path from the root of the document to the
value that didn't fit, each step in turn (empty for the root itself), and what was wrong with it.

## `EncodeError`

```nanyx fragment
type EncodeError = #cannotEncode(path: list(PathStep), message: string)
```

Why [`encode`](json-reflection#encode) wrote no text: `#cannotEncode`, with the path from the value's root to a part of
it that can't be written as JSON, a function or a nominal type, and why.

## `Decoder`

```nanyx fragment
type Decoder(a) = private (run: (Json -> #some(a) | #failed(list(PathStep), string)), absent: #some(a) | #none)
```

What reads an `a` from a `Json` value, or says why it can't. The decoders here make one,
[`decoderFor`](json-reflection#decoderfor) makes one for any type by walking it, and [`decodeWith`](#decodewith) and [`decodeValueWith`](#decodevaluewith)
run one. A missing member is the decoder's to read too: only
[`optional`](#optional) reads one, as `#none`.

## `string`

```nanyx fragment
def string: Decoder(string)
```

Reads a string.

## `float`

```nanyx fragment
def float: Decoder(float)
```

Reads a number, as JSON holds one: a `float`.

## `int`

```nanyx fragment
def int: Decoder(int)
```

Reads a number that is whole, and in the range of `int`, as an `int`: `2.5` is an error.

## `bool`

```nanyx fragment
def bool: Decoder(bool)
```

Reads `true` or `false`.

## `value`

```nanyx fragment
def value: Decoder(Json)
```

Reads any value, as it is: for a part of a document that is read later, or not at all.

## `list`

```nanyx fragment
def list: Decoder(a) -> Decoder(list(a))
```

Reads an array, each element by `decoder`: `json.list(json.int)` reads `[1, 2, 3]`.

## `dict`

```nanyx fragment
def dict: Decoder(a) -> Decoder(map(string, a))
```

Reads an object as a map, each member's value by `decoder`, in the order the object has them:
for an object whose members aren't known ahead, as `{"en": "Hello", "fr": "Bonjour"}`.

## `optional`

```nanyx fragment
def optional: Decoder(a) -> Decoder(#some(a) | #none)
```

Reads `null` as `#none`, and anything else by `decoder`, as `#some`. A member that [`field`](#field)
finds missing reads as `#none` too, so `json.field("nickname", json.optional(json.string))` reads
an object with no `nickname`.

## `field`

```nanyx fragment
def field: string, Decoder(a) -> Decoder(a)
```

Reads the member `name` of an object by `decoder`. A missing member is an error, but for a
decoder that reads one as a value, as [`optional`](#optional) does.

## `succeed`

```nanyx fragment
def succeed: a -> Decoder(a)
```

The decoder that reads `x` from any value, whatever it is.

## `fail`

```nanyx fragment
def fail: string -> Decoder(a)
```

The decoder that reads no value, but fails with `message`: for a value that is the right kind
but not one of those allowed, as an unknown tag.

## `convert`

```nanyx fragment
def convert: Decoder(a), (a -> b) -> Decoder(b)
```

Reads a value by `decoder`, then gives `f` of it. A member `decoder` reads as missing is `f` of
what it reads it as.

## `andThen`

```nanyx fragment
def andThen: Decoder(a), (a -> Decoder(b)) -> Decoder(b)
```

Reads a value by `decoder`, then reads the same value again by the decoder `next` makes of
what the first read: for a value whose shape depends on part of it, as a tagged union's does on
its tag.

## `oneOf`

```nanyx fragment
def oneOf: list(Decoder(a)) -> Decoder(a)
```

Reads a value by the first of `decoders` that reads it. When none does, the error is the one
that got furthest into the value, the first of those that tie.

## `DecodeRecord`

```nanyx fragment
context DecodeRecord = (
  try: [c] Decoder(a), ([c] a -> Decoder(b)) -> Decoder(b)
  return: a -> Decoder(a)
)
```

The keywords of a [`record`](#record) block: `try` reads a value by a decoder, and `return` gives the
value the block reads.

- `try`: Reads the value by the decoder, and goes on with what it read.
- `return`: The value the block reads.

## `record`

```nanyx fragment
def record: [c] ([DecodeRecord, c] () -> Decoder(b)) -> Decoder(b)
```

A decoder written as a block that reads a value a part at a time: each `try` reads the value by
a decoder, usually a [`field`](#field), and `return` says what the block reads from what they read.

```nanyx fragment
def user: json.Decoder(User) = json.record {
  def name = try json.field("name", json.string)
  def age = try json.field("age", json.int)
  return User(name = name, age = age)
}
```

The first `try` that fails ends the block with its error. A `try` can read by any decoder,
one chosen by what an earlier `try` read among them, as for a tagged union.

## `decodeValueWith`

```nanyx fragment
def decodeValueWith: Json, Decoder(a) -> #some(a) | #doesNotFit(path: list(PathStep), message: string)
```

What `decoder` reads from `value`, or a [`DecodeError`](#decodeerror) with the path to what didn't fit, and
why.

## `decodeWith`

```nanyx fragment
def decodeWith: string, Decoder(a) -> #some(a) | #doesNotFit(path: list(PathStep), message: string) | #invalidJson(position: int, line: int, column: int, message: string)
```

What `decoder` reads from the JSON `text`, or why it reads nothing: an `#invalidJson` for text
that isn't JSON, which says where in the text, or a [`DecodeError`](#decodeerror) for a value that doesn't fit.

## `pathText`

```nanyx fragment
def pathText: list(PathStep) -> string
```

`steps` as a path is written: a member by its name, after a `.` but at the start, and an
element by its index in brackets, as `users[2].age`. The root, with no steps, is `""`.

## `describe`

```nanyx fragment
def describe: #doesNotFit(path: list(PathStep), message: string) | #cannotEncode(path: list(PathStep), message: string) | #invalidJson(position: int, line: int, column: int, message: string) -> string
```

`e` as a message writes it. A [`DecodeError`](#decodeerror) or an [`EncodeError`](#encodeerror) is its path, then what was
wrong there, as `users[2].age: Expected a whole number, found 2.5`, or only what was wrong for
the root. A `JsonError` is what was wrong, then where, as `Expected a value, found the end of
the text, at line 1, column 5`.

## `Encoder`

```nanyx fragment
type Encoder(a) = a -> Json
```

What writes an `a` as a `Json` value: any function that does. The encoders that build one are
in the module `nanyx.json/encode`.

## `encodeWith`

```nanyx fragment
def encodeWith: a, Encoder(a) -> string
```

`value` as JSON text, written by `encoder` and printed compactly as [`print`](json#print) prints.

<!-- End of the generated reference. -->
