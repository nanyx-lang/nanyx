---
title: json, decoders
description: Reading JSON into types of your own, and writing them back
order: 15.6
---

A `Decoder(a)` reads an `a` out of a `Json` value, and says where a document doesn't fit. The decoders are in
`nanyx.json/json` with the rest of [json](json), and the encoders, which go the other way, in [encode](encode).

Some decoders read one kind of value each: `string`, `int` (a whole number), `float` and `bool`, and `value` reads any
value as it is. The rest build larger decoders from smaller: `list`, `dict`, `optional`, `field`, `oneOf`, `convert`,
`andThen`, and `record`, a builder that reads a record a member at a time, each with `try`, and gives back what it
read with `return`. `json.decode` parses text and runs a decoder on it, and `json.decodeValue` runs one on a value
already parsed.

When a value doesn't fit, the `DecodeError` says where, by the path from the document's root to it, which
`json.pathText` writes as `users[2].age`, and what was wrong there; `json.describe` writes both. A decoder takes time
in proportion to the size of what it reads, and reads a long list in constant stack.

```nanyx
::dependency nanyx.json 0.1.0

import (
  nanyx.json/encode as encode
  nanyx.json/json as json
)

type User = (name: string, age: int)

def user: json.Decoder(User) = json.record {
  def name = try json.field("name", json.string)
  def age = try json.field("age", json.int)
  return User(name = name, age = age)
}

def userEncoder: json.Encoder(User) = encode.object(
  [
    encode.field("name", { u -> u.name }, encode.string)
    encode.field("age", { u -> u.age }, encode.int)
  ]
)

match json.decode('[{"name": "Ada", "age": 36}, {"name": "Bob", "age": 2.5}]', json.list(user))
  | #some(users) -> println(json.encode(users, encode.list(userEncoder)))
  | #error(e) -> println(json.describe(e))

-- [1].age: Expected a whole number, found 2.5
```

A tagged union is read by the decoder a field of it chooses: a `try` reads by any decoder, including one picked by what
an earlier `try` read.

```nanyx
::dependency nanyx.json 0.1.0

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

match json.decode('{"kind": "square", "side": 2}', shape)
  | #some(#square(side)) -> println("A square of side {side}")
  | #some(_) -> println("Another shape")
  | #error(e) -> println(json.describe(e))

-- A square of side 2.0
```

# Reference

`nanyx.json` is published from its own repository, [nanyx-lang/json](https://github.com/nanyx-lang/json), so its
reference is the doc comments in its sources, [`src/json.typed.nyx`](https://github.com/nanyx-lang/json/blob/main/src/json.typed.nyx),
which the editor shows when you hover over a name.
