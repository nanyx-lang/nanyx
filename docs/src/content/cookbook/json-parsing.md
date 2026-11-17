---
title: Parse JSON Data
description: Working with JSON in Nanyx
order: 2
---

The compiler ships a package for reading and writing JSON, `nanyx.json`. It reads text into a `Json` value, a tag union
you match on like any other, and writes one back out. Its reference is the [json](../stdlib/json) page.

# Setup

```bash
nanyx new json_parsing
cd json_parsing
nanyx install nanyx.json
```

A script names it in a `::dependency` line instead:

```nanyx fragment
::dependency nanyx.json
```

Import its module under a name, `json`, so that its `print` doesn't clash with the console's.

# Reading JSON

`json.parse` gives back `#some(Json)`, or `#error(JsonError)` when the text isn't JSON. An object is a
`map(string, Json)`, so `map.get` looks a key up, and one pattern can take the result apart through as many tags as
it needs:

```nanyx
::dependency nanyx.json

import nanyx.json/json as json

-- A single-quoted string is raw, so the braces are not interpolated
def data = '{"name": "Alice", "age": 30}'

match json.parse(data)
  | #some(#object(user)) -> (match (map.get(user, "name"), map.get(user, "age"))
    | (#some(#string(name)), #some(#number(age))) -> println("Hello, {name}, aged {age}!")
    | _ -> println("Who are you?"))
  | #some(_) -> println("Expected an object")
  | #error(e) -> println("Invalid JSON at line {e.line}, column {e.column}: {e.message}")

-- Hello, Alice, aged 30.0!
```

# Writing JSON

Build a `Json` value from its tags, and write it with `json.print`, compactly, or `json.printPretty`, a member or
element to a line. An object's members are written in the order its map has them, which for a map literal is the
order they are written in:

```nanyx fragment
def user = #object(["name" => #string("Alice"), "age" => #number(30.0), "active" => #bool(true)])

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

# Decoding into records

A decoder reads a `Json` value into a type of your own, and says where a document didn't fit. `json.record` reads a
record a member at a time, each with `try`, and an encoder from `nanyx.json/encode` writes it back:

```nanyx
::dependency nanyx.json

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

def data = '{"users": [{"name": "Alice", "age": 30}, {"name": "Bob", "age": "25"}]}'

match json.decode(data, json.field("users", json.list(user)))
  | #some(users) -> println(json.encode(users, encode.list(userEncoder)))
  | #error(e) -> println(json.describe(e))

-- users[1].age: Expected a whole number, found a string
```

The [decoders](../stdlib/json-typed) page lists the rest: lists, maps, optional members, tagged unions and more.
