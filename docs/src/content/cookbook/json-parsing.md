---
title: Parse JSON Data
description: Working with JSON in Nanyx
order: 2
---

`nanyx.json` is a package for reading and writing JSON, published to the package index from
[nanyx-lang/json](https://github.com/nanyx-lang/json). It reads text into a `Json` value, a tag union you match on like
any other, and writes one back out. Its guide is the [json](../stdlib/json) page.

# Setup

```bash
nanyx new json_parsing
cd json_parsing
nanyx install nanyx.json
```

`nanyx install` adds `(package = "nanyx.json", version = "0.5")` to the manifest's `dependencies`, locks the newest
0.5 version in `nanyx.lock` and downloads it. A script names the exact version it uses in a `::install` line
instead, which `nanyx run` downloads:

```nanyx fragment
::install nanyx.json 0.5.1
```

Import its module under a name, `json`, so that its `print` doesn't clash with the console's.

# Reading JSON

`json.parse` gives back `#some(Json)`, or `#invalidJson` with where and why when the text isn't JSON. An object is a
list of its members, each a key and a value, in the order they are written, so `json.member` looks a key up, and one
pattern can take the result apart through as many tags as it needs:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

-- A single-quoted string is raw, so the braces are not interpolated
def data = '{"name": "Alice", "age": 30}'

match json.parse(data)
  | #some(#object(user)) -> (match (json.member(user, "name"), json.member(user, "age"))
    | (#some(#string(name)), #some(#number(age))) -> println("Hello, {name}, aged {age}!")
    | _ -> println("Who are you?"))
  | #some(_) -> println("Expected an object")
  | #invalidJson(e) -> println("Invalid JSON at line {e.line}, column {e.column}: {e.message}")

-- Hello, Alice, aged 30.0!
```

# Writing JSON

Build a `Json` value from its tags, and write it with `json.print`, compactly, or `json.printPretty`, a member or
element to a line. An object's members are written in the order they are in its list.

```nanyx fragment
def user = #object([("name", #string("Alice")), ("age", #number(30.0)), ("active", #bool(true))])

println(json.print(user))
-- {"name":"Alice","age":30,"active":true}

println(json.printPretty(user, indent = 2))
-- {
--   "name": "Alice",
--   "age": 30,
--   "active": true
-- }
```

# Working with arrays

An array is a `list(Json)`, so the list functions build and take one apart:

```nanyx fragment
def names = users \map { user -> #string(user.name) }

println(json.print(#array(names)))
-- ["Alice","Bob"]
```

# Reading and writing records

`json.encode` writes a value of any type, and `json.decode` reads text into the type it is given, by walking the type,
with no decoder or encoder written for it. A record is an object, a tag with a payload `{"tag": payload}` and one
without its name, and an option is its value or `null`, left out when it is `#none`. A field's key is its name, or the
one a `@json(name = "...")` attribute before it gives. `json.encode` gives `#some` of the text, or `#cannotEncode` for
a part that can't be written, a function or a nominal type whose structure it can't see, and `json.decode` gives
`#some` of the value, or `#doesNotFit` when a document doesn't fit; `json.describe` says where. A record's fields are
written in the order its type declares them:

```nanyx
::install nanyx.json 0.5.1

import nanyx.json/json as json

type Role = #admin | #member(team: string)

type User = (
  name: string
  age: int
  @json(name = "e_mail")
  email: #some(string) | #none
  role: Role
)

def ada = User(name = "Ada", age = 36, email = #none, role = #member(team = "core"))

match json.encode(ada)
  | #some(text) -> println(text)
  | failure -> println(json.describe(failure))

def data = '{"users": [{"name": "Alice", "age": 30, "role": "admin"}, {"name": "Bob", "age": "25", "role": "admin"}]}'

match json.decode(type (users: list(User)), data)
  | #some(read) -> println(json.encode(read.users) \orCrash("users are written"))
  | failure -> println(json.describe(failure))

-- {"name":"Ada","age":36,"role":{"member":{"team":"core"}}}
-- users[1].age: Expected a whole number, found a string
```

# Writing a decoder of your own

To read or write a type another way, build a decoder from smaller ones. `json.record` reads a record a member at a
time, each with `try`, `json.decoderFor(type T)` reads a part by walking its type, and `json.decodeWith` runs the
decoder; encoders from `nanyx.json/encode` go the other way, run by `json.encodeWith`:

```nanyx
::install nanyx.json 0.5.1

import (
  nanyx.json/encode as encode
  nanyx.json/json as json
)

type Point = (x: int, y: int)

type Place = (name: string, at: Point)

-- a place is written as {"title": ..., "at": ...}, not under its field's name
def place: json.Decoder(Place) = json.record {
  def name = try json.field("title", json.string)
  def at = try json.field("at", json.decoderFor(type Point))
  return Place(name = name, at = at)
}

def placeEncoder: json.Encoder(Place) = encode.object(
  [
    encode.field("title", { p -> p.name }, encode.string)
    encode.field("at", { p -> p.at }, json.encoderFor(type Point))
  ]
)

match json.decodeWith('[{"title": "home", "at": {"x": 1, "y": 2}}]', json.list(place))
  | #some(places) -> println(json.encodeWith(places, encode.list(placeEncoder)))
  | failure -> println(json.describe(failure))

-- [{"title":"home","at":{"x":1,"y":2}}]
```

The [decoders](../stdlib/json-typed) page lists the rest: lists, maps, optional members, tagged unions and more.
