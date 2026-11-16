---
title: Parse JSON Data
description: Working with JSON in Nanyx
order: 2
---

Nanyx provides a `json` module for encoding and decoding JSON.

# Setup

```bash
nanyx new json_parsing
cd json_parsing
nanyx install nanyx.web
```

```nanyx
module main

import nanyx.web/json

type User = (name: string, age: int)

def decodeUser: string -> Result(User, DecodeError) = { data ->
  data
    \json.decode(
      json.record(name = json.field("name", json.string), age = json.field("age", json.int))
    )
}

@entry
def main = {
  -- A single-quoted string is raw, so the braces are not interpolated
  def data = '{"name": "Alice", "age": 30}'

  match decodeUser(data)
    | #some(user) -> println("Hello, {user.name}!")
    | #error(_) -> println("Invalid JSON")
}
```

# Encoding JSON

```nanyx
def jsonString =
  json.object([
    ("name", json.string("Alice"))
    ("age", json.int(30))
    ("active", json.bool(true))
  ])
  \json.toString

-- {"name":"Alice","age":30,"active":true}
```

# Working with arrays

```nanyx
def usersJson = 
  users \map { user ->
    json.object([
      ("name", json.string(user.name))
      ("age", json.int(user.age))
    ])
  }
  \json.array
  \json.toString
```
