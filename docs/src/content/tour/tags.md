---
title: "Tags"
description: "Literal tags and tag unions"
order: 3
---

Tags are a foundational feature in Nanyx, somewhat similar to symbols/atoms in Ruby and Erlang or string literal types in TypeScript and Scala. They are the building blocks of _tag unions_, where a tag represents a single named case and tags can be combined into types that represent a choice between cases, with each case optionally carrying its own data. This gives Nanyx a lightweight and structural way to model enums, alternatives, and discriminated unions without introducing a separate kind of type declaration.

A tag is both a value and its own type. The tag name identifies the exact value, which makes tags useful for precise modelling and pattern matching. Tags use camel-cased names in the same way as other value identifiers, such as `#ok`, `#error`, `#notFound`.

> Tags that start with a capital letter are reserved for use as custom patterns. For the full model and examples, see [Custom patterns](../advanced/custom-patterns).

# Tags as values and types

You can use tags directly as values:

```nanyx
def x = #myTag
```

You can also annotate with a specific tag type:

```nanyx
def y: #mySecondTag = #mySecondTag
```

# Tag unions

Tag types are most useful when combined into unions. A tag union is a type that can be one of several tagged cases, and can be used anywhere a type is expected. For example, a function that can return either an `#ok` or an `#error` tag can be typed as follows:

```nanyx
def f: string -> #ok | #error = { s ->
  ...
}
```

```nanyx
type Color = #red | #green | #blue

def myFavoriteColor: Color = #green
```

Using plain tags gives you an enum-like shape (while remaining structural).

You rarely have to write a union out by hand. When different branches of a function return different tags, the compiler infers the union for you; see [Inferring tag unions](./type-inference-and-annotations#inferring-tag-unions).

# Payloads

Tags can also carry payloads, allowing them to function as discriminated unions or sum types:

```nanyx
type Color =
  | #red
  | #green
  | #blue
  | #custom(string)
```

In type syntax, a tag with payload is written `#tagName(PayloadType)`. The payload can be any type, including a record or another tag union:

```nanyx
type Shape =
  | #circle(float)
  | #rectangle(width: float, height: float)
```

```nanyx
type AuthResult =
  | #authenticated(username: string)
  | #rejected(#invalidCredentials | #expiredToken)
```

A tag that carries the unit value `()` is written with empty brackets, in values and in types alike, just as a function that takes nothing is called `f()`. `#some()` is `#some(())`:

```nanyx
def saved: #some() | #error(string) = #some()
```

# Tags for function flags

Tag unions are great for readable flags and mode parameters. They make legal options explicit in the function type:

```nanyx
def openFile: (string, (#read | #readwrite)) -> File
  = { fileName, mode ->
    ...
  }

def file = openFile("file.txt", #readwrite)
```

# Polymorphic tag unions

Because tag unions are structural, functions can be written that require specific cases while allowing unknown cases to pass through. This is a common pattern for reusable APIs.

For example, the `nanyx/option` module contains a `map` function that transforms only the `#some` case of a given union type and preserves everything else:

```nanyx
def Option.map
  : (#some(a) | r), (a -> b) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }
```

This preserves non-success cases while still transforming success values:

```nanyx
def x: (#some(string) | #notFound) = ...
def y: (#some(int) | #divideByZero) = ...

x \Option.map { .length }
y \Option.map { \Math.abs }
```

For broader union and matching patterns, see [Pattern matching](./pattern-matching) and [Types](./types).

# Runtime behavior

Despite their similarity to string literal types, tags are **not** strings at runtime. A tag is its own runtime value kind, so `#ok` is not the same thing as `"ok"`.

That said, converting a tag to text gives the expected name:

```nanyx
def t = #readwrite

t.toString   -- "#readwrite"
```

This is useful for logging and debugging, but it does not change the underlying runtime type of the value.
