---
title: encode
description: Writing types of your own as JSON
order: 15.7
package: nanyx.json 0.5.1
---

`json.encode(value)` writes a value of any type as JSON text with nothing written for it, by walking its type: see
[reflection](json-reflection). An encoder is for writing a type another way, the other way from the
[decoders](json-typed): it writes a value of your own type as a `Json` value, and `json.encodeWith` writes a value as
JSON text by one. An encoder is any function to a `Json` value, so it can be written as one, or built from the
encoders here, and `json.encoderFor(type T)` writes a part as `json.encode` would.

Here a shape is written with its kind in a member of its own, as the decoders page reads one, where `json.encode`
writes a tag as an object of one member:

```nanyx
::install nanyx.json 0.5.1

import (
  nanyx.json/encode as encode
  nanyx.json/json as json
)

type Shape = #circle(float) | #square(float)

type Drawing = (title: string, shapes: list(Shape))

def shape: json.Encoder(Shape) = {
  | #circle(r) -> #object([("kind", #string("circle")), ("radius", encode.float(r))])
  | #square(s) -> #object([("kind", #string("square")), ("side", encode.float(s))])
}

def drawing: json.Encoder(Drawing) = encode.object(
  [
    encode.field("title", { d -> d.title }, encode.string)
    encode.field("shapes", { d -> d.shapes }, encode.list(shape))
  ]
)

def sketch = Drawing(title = "Sketch", shapes = [#circle(1.5), #square(2.0)])

println(json.encode(sketch) \orCrash("a drawing is written"))
println(json.encodeWith(sketch, drawing))

-- {"title":"Sketch","shapes":[{"circle":1.5},{"square":2}]}
-- {"title":"Sketch","shapes":[{"kind":"circle","radius":1.5},{"kind":"square","side":2}]}
```

# Reference

<!-- The reference below is generated from the doc comments of nanyx.json 0.5.1, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->

Encoders: a value of a type of your own written as JSON, the other way from `json`'s decoders.

An [`Encoder(a)`](json-typed#encoder) is a function from an `a` to a `Json` value, so any such
function is one. The encoders here write one kind of value each ([`string`](#string), [`int`](#int),
[`float`](#float), [`bool`](#bool)), and the rest build larger ones from smaller: [`list`](#list), [`dict`](#dict),
[`optional`](#optional), and [`object`](#object), which writes a record a member at a time, each a [`field`](#field) or an
[`optionalField`](#optionalfield). `json.encodeWith` writes a value as JSON text by an encoder. An encoder is
only needed to write a value differently from `json.encode`, which writes any value by walking
its type, and `json.encoderFor(type T)` is that walk as an encoder, for a part of a value.

```nanyx fragment
type User = (name: string, age: int)

def user: json.Encoder(User) = encode.object(
  [
    encode.field("name", { u -> u.name }, encode.string)
    encode.field("age", { u -> u.age }, encode.int)
  ]
)

json.encodeWith(User(name = "Ada", age = 36), user)
```

The encoders are in a module of their own, `nanyx.json/encode`, imported `as encode`, since their
names are the decoders' in `nanyx.json/json`.

## `string`

```nanyx fragment
def string: Encoder(string)
```

Writes a string.

## `int`

```nanyx fragment
def int: Encoder(int)
```

Writes an `int` as a number.

## `float`

```nanyx fragment
def float: Encoder(float)
```

Writes a `float` as a number. NaN and infinity, which JSON can't write, are printed as `null`.

## `bool`

```nanyx fragment
def bool: Encoder(bool)
```

Writes `true` or `false`.

## `list`

```nanyx fragment
def list: Encoder(a) -> Encoder(list(a))
```

Writes a list as an array, each element by `encoder`.

## `dict`

```nanyx fragment
def dict: Encoder(a) -> Encoder(map(string, a))
```

Writes a map as an object, each value by `encoder`, in the map's order.

## `optional`

```nanyx fragment
def optional: Encoder(a) -> Encoder(#some(a) | #none)
```

Writes `#none` as `null`, and `#some(x)` as `encoder` writes `x`.

## `Member`

```nanyx fragment
type Member(a) = private (name: string, write: (a -> #some(Json) | #none))
```

A member of an object that [`object`](#object) writes for a value of type `a`: its name, and its value,
or none, for a member that is left out.

## `field`

```nanyx fragment
def field: string, (a -> b), Encoder(b) -> Member(a)
```

The member `name`, whose value is `encoder` of `get` of the value: `encode.field("age", { u ->
u.age }, encode.int)`.

## `optionalField`

```nanyx fragment
def optionalField: string, (a -> #some(b) | #none), Encoder(b) -> Member(a)
```

The member `name`, written as [`field`](#field) writes one when `get` gives `#some`, and left out when
it gives `#none`: for an optional member, which [`optional`](#optional) would write as `null` instead.

## `object`

```nanyx fragment
def object: list(Member(a)) -> Encoder(a)
```

Writes a value as an object of `members`, in their order.

<!-- End of the generated reference. -->
