---
title: json, reflection
description: Writing and reading any type as JSON, with no encoder or decoder written for it
order: 15.55
package: nanyx.json 0.5.1
---

`json.encode` writes a value of any type as JSON text, and `json.decode` reads text into the type it is given, by
walking the type's description, as [inspecting a type](../tour/functions#inspecting-a-type) does. Nothing is written for
the type: no encoder and no decoder, only a `@json(name = "...")` attribute for a field whose key isn't its name. They
are in `nanyx.json/json` with the rest of [json](json), and what `encode` writes, `decode` reads back.

A record is an object, a tag with no payload its name, a tag with one an object of one member, and a field that is
`#none` is left out:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

type Shape = #point | #circle(float) | #rect(width: float, height: float)

type Drawing = (
  title: string
  @json(name = "made_by")
  author: #some(string) | #none
  shapes: list(Shape)
)

def drawing = Drawing(
  title = "Sketch"
  author = #none
  shapes = [#point, #circle(1.5), #rect(width = 2.0, height = 1.0)]
)

match json.encode(drawing)
  | #some(text) -> println(text)
  | failure -> println(json.describe(failure))

def plan = '{"title": "Plan", "made_by": "Ada", "shapes": [{"circle": 2}]}'

match json.decode(type Drawing, plan)
  | #some(read) -> println("{read.title} by {read.author}")
  | failure -> println(json.describe(failure))

-- {"title":"Sketch","shapes":["point",{"circle":1.5},{"rect":{"width":2,"height":1}}]}
-- Plan by #some(Ada)
```

A record is written as an object, its fields in the order the type writes them, since an object keeps its members in
order (see [json](json)).

A failure is a tag of its own, which `json.describe` writes: `#invalidJson` for text that isn't JSON, `#doesNotFit` for
a value that doesn't fit the type, and `#cannotEncode` for a part of a value that JSON can't hold, each with where:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

type Point = (x: int, y: int)

def show = { text ->
  match json.decode(type list(Point), text)
    | #some(points) -> println("{list.length(points)} points")
    | failure -> println(json.describe(failure))
}

show('[{"x": 1, "y": 2}, {"x": 3, "y": 4}]')
show('[{"x": 1, "y": 2}, {"x": 3, "y": 4.5}]')
show('[{"x": 1, "y": 2},')

type Button = (label: string, onClick: () -> ())

match json.encode(Button(label = "OK", onClick = { () }))
  | #some(text) -> println(text)
  | failure -> println(json.describe(failure))

-- 2 points
-- [1].y: Expected a whole number, found 4.5
-- Expected a value, found the end of the text, at line 1, column 19
-- onClick: A function can't be written as JSON
```

To write or read a type another way, as nested differently or with a tag in a member of its own, build a
[decoder](json-typed) or an [encoder](encode) for it. `json.decoderFor(type T)` and `json.encoderFor(type T)` are the walk
as a decoder and an encoder, for the parts of it that are written as `encode` writes them.

Until compiler fixes land, a recursive type, such as a tree, or a field of type `Json`, isn't walked right
([#2378](https://github.com/nanyx-lang/nanyx/issues/2378)); `json.encode` can't write a nominal type, even one of the
module that calls it ([#2382](https://github.com/nanyx-lang/nanyx/issues/2382)), which
`json.encodeWith(x, json.encoderFor(type T))` can; and a set can't be read.

# Reference

<!-- The reference below is generated from the doc comments of nanyx.json 0.5.1, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->

## `encode`

```nanyx fragment
def encode: [Typed(a)] a -> #some(string) | #cannotEncode(path: list(PathStep), message: string)
```

`value` as JSON text, with no encoder written for it, printed compactly as [`print`](json#print) prints:
`json.encode((name = "Ada", age = 36))` is `#some("\{"name":"Ada","age":36}")`. The value is
written by walking its type. A record is an object of its fields, each under its name, or the
`name` of a `@json(name = "...")` attribute written before it, and a tuple is an array. A tag
with no payload is its name, as a string, and a tag with one is an object with one member, the
payload under the tag's name, as `{"circle": 1.5}`. An option, `#some(x) | #none`, is `x` or
`null`, and a record's field that is `#none` is left out. A list, an array and a set are arrays,
a map with string keys is an object, and any other map an array of `[key, value]` pairs. Numbers
of every size are numbers, and `()` is `null`.

A function and a nominal type can't be written: they are an `EncodeError`, with the path to
them. A nominal type shows its structure only to its own module, and `encode` learns the value's
type from the prelude's `Typed`, so not even a nominal type of your own module is shown to it.
Write one with `json.encodeWith(x, json.encoderFor(type T))`, which sees `T` from where
`type T` is written, or with an `Encoder` of your own.

## `encodeValue`

```nanyx fragment
def encodeValue: [Typed(a)] a -> #some(Json) | #cannotEncode(path: list(PathStep), message: string)
```

`value` as a `Json` value, with no encoder written for it, written by walking its type as
[`encode`](#encode) writes it: `json.encodeValue((name = "Ada", tags = ["x"]))` is
`#some(#object([("name", #string("Ada")), ("tags", #array([#string("x")]))]))`. For a value to
put in a larger `Json` value, or to print with [`printPretty`](json#printpretty). A function and a nominal type
can't be written: they are an `EncodeError`, with the path to them.

## `encoderFor`

```nanyx fragment
def encoderFor: Type(a) -> Encoder(a)
```

The encoder that writes a value of the type `t` as [`encode`](#encode) does: for writing a part of a
value by its type, within an `Encoder` of your own. An encoder can't fail, so a value that
`encode` gives an `EncodeError` for, a function or a nominal type `t` doesn't show the
structure of, crashes it.

## `decoderFor`

```nanyx fragment
def decoderFor: Type(a) -> Decoder(a)
```

The decoder that reads a value of the type `t` by walking it, as [`decode`](#decode) does: for reading a
part of a value by its type, within a `Decoder` of your own. A member that is missing reads as
`#none` when `t` is an option's type, and is an error otherwise.

## `decodeValue`

```nanyx fragment
def decodeValue: Type(a), Json -> #some(a) | #doesNotFit(path: list(PathStep), message: string)
```

A value of the type given first, read from the `Json` value by walking the type, as [`decode`](#decode) reads one, or a
`DecodeError` with the path to what didn't fit, and why.

## `decode`

```nanyx fragment
def decode: Type(a), string -> #some(a) | #doesNotFit(path: list(PathStep), message: string) | #invalidJson(position: int, line: int, column: int, message: string)
```

A value of the type given first, read from the JSON text, with no decoder written for it:
`json.decode(type User, text)`. The type is walked as [`encode`](#encode) walks it, so what
`encode` writes, `decode` reads back. A missing member is an error, but for a field whose type is
an option, which reads a missing member and `null` as `#none`. A number read as an `int` must be
whole, and one read as a sized number must be in its range.

Text that isn't JSON is an `#invalidJson`, and a value that doesn't fit a `DecodeError`, with
the path to it. A set, a map with keys other than strings, a function, and a nominal type of
another module can't be read; read one with a `Decoder` of your own instead.

<!-- End of the generated reference. -->
