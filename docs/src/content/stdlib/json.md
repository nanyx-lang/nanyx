---
title: json
description: Reading and writing JSON
order: 15.5
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

`nanyx.json` is published from its own repository, [nanyx-lang/json](https://github.com/nanyx-lang/json), so its
reference is the doc comments in its sources, [`src/json.nyx`](https://github.com/nanyx-lang/json/blob/main/src/json.nyx),
which the editor shows when you hover over a name.
