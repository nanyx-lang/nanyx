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
`map(string, Json)`, so `map.get` looks a key up:

```nanyx
module main

import nanyx.json/json as json

@entry
def main = {
  -- A single-quoted string is raw, so the braces are not interpolated
  def data = '{"name": "Alice", "age": 30}'

  match json.parse(data)
    | #some(#object(user)) ->
      match map.get(user, "name")
        | #some(#string(name)) -> println("Hello, {name}!")
        | _ -> println("Who are you?")
    | #some(_) -> println("Expected an object")
    | #error(e) -> println("Invalid JSON at line {e.line}, column {e.column}: {e.message}")
}
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

Reading a `Json` value into a record of your own, with decoders that say where a document didn't fit
(`users[2].age`), is planned ([Files, HTTP and JSON](../work-in-progress/files-http-json#json-nanyxjson)). Until then,
match on the value as above.
