---
title: encode
description: Writing types of your own as JSON
order: 15.7
---

An encoder writes a value of your own type as a `Json` value, the other way from the
[decoders](json-typed). `json.encode` writes a value as JSON text by one.

```nanyx
::dependency nanyx.json 0.1.0

import (
  nanyx.json/encode as encode
  nanyx.json/json as json
)

type User = (name: string, age: int, nickname: #some(string) | #none)

def user: json.Encoder(User) = encode.object(
  [
    encode.field("name", { u -> u.name }, encode.string)
    encode.field("age", { u -> u.age }, encode.int)
    encode.optionalField("nickname", { u -> u.nickname }, encode.string)
  ]
)

println(json.encode(User(name = "Ada", age = 36, nickname = #none), user))

-- {"name":"Ada","age":36}
```

# Reference

`nanyx.json` is published from its own repository, [nanyx-lang/json](https://github.com/nanyx-lang/json), so its
reference is the doc comments in its sources, [`src/encode.nyx`](https://github.com/nanyx-lang/json/blob/main/src/encode.nyx),
which the editor shows when you hover over a name.
